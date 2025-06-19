using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MarkSubsystem.Data;
using MarkSubsystem.DTO;
using MarkSubsystem.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MarkSubsystem.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TestQualityController : ControllerBase
{
    private readonly UsersDbContext _usersDbContext;
    private readonly HttpClient _httpClient;
    private readonly string _testManagementUrl = "http://InterpretatorService_2:8080/api/TestManagement";
    private readonly string _debugLogPath = "/app/logs/debug.txt";

    public TestQualityController(UsersDbContext usersDbContext, HttpClient httpClient)
    {
        _usersDbContext = usersDbContext;
        _httpClient = httpClient;
    }

    [HttpGet("get-tests-for-user/{userId}/{algoId}")]
    public async Task<IActionResult> GetTestsForUser(int userId, int algoId)
    {
        try
        {
            var userAbilities = await _usersDbContext.UserTestAbilities
                .Where(uta => uta.UserId == userId)
                .ToDictionaryAsync(uta => uta.TestId, uta => uta.Ability);

            var testResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-tests?algoId={algoId}");
            if (!testResponse.IsSuccessStatusCode)
            {
                await LogError($"Failed to get tests: algoId={algoId}, status={testResponse.StatusCode}");
                return StatusCode((int)testResponse.StatusCode, "Error fetching tests.");
            }

            var tests = await testResponse.Content.ReadFromJsonAsync<List<Test>>();
            if (tests == null || !tests.Any())
            {
                await LogDebug($"No tests found for algoId={algoId}");
                return Ok(new List<int>());
            }

            var suitableTestIds = new List<int>();
            foreach (var test in tests)
            {
                var userAbility = userAbilities.GetValueOrDefault(test.TestId, 0f);
                if (Math.Abs(test.difficult - userAbility) < 0.1f)
                {
                    suitableTestIds.Add(test.TestId);
                }
            }

            await LogSuccess($"Found {suitableTestIds.Count} suitable tests for userId={userId}, algoId={algoId}");
            return Ok(suitableTestIds);
        }
        catch (Exception ex)
        {
            await LogError($"Exception in GetTestsForUser: {ex.Message}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("upload-quality-parameters")]
    public async Task<IActionResult> UploadQualityParameters([FromBody] List<UpdateStepResponseDto> updates)
    {
        try
        {
            foreach (var update in updates)
            {
                var testResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-test/{update.TestId}");
                if (!testResponse.IsSuccessStatusCode)
                {
                    await LogError($"Test not found: testId={update.TestId}, status={testResponse.StatusCode}");
                    continue;
                }

                var test = await testResponse.Content.ReadFromJsonAsync<Test>();
                if (test == null || test.AlgoId != update.AlgoId)
                {
                    await LogError($"Invalid test or AlgoId mismatch: testId={update.TestId}, algoId={update.AlgoId}");
                    continue;
                }

                var stepResponsesResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-step-responses/{update.TestId}");
                var stepResponsesContent = await stepResponsesResponse.Content.ReadAsStringAsync();
                var testStepResponses = JsonSerializer.Deserialize<List<TestStepResponse>>(stepResponsesContent) ?? new List<TestStepResponse>();

                float testDifficultySum = 0f;
                foreach (var stepResult in update.StepResults)
                {
                    var stepResponse = testStepResponses.FirstOrDefault(tsr => tsr.TestId == update.TestId && tsr.AlgoStep == stepResult.AlgoStep);
                    if (stepResponse == null)
                    {
                        stepResponse = new TestStepResponse
                        {
                            TestId = update.TestId,
                            AlgoStep = stepResult.AlgoStep,
                            CorrectCount = stepResult.IsCorrect ? 1 : 0,
                            IncorrectCount = stepResult.IsCorrect ? 0 : 1
                        };
                        testStepResponses.Add(stepResponse);
                    }
                    else
                    {
                        stepResponse.CorrectCount += stepResult.IsCorrect ? 1 : 0;
                        stepResponse.IncorrectCount += stepResult.IsCorrect ? 0 : 1;
                    }

                    float stepDifficulty = stepResponse.IncorrectCount == 0 ? float.MaxValue :
                        Math.Max((float)stepResponse.CorrectCount / stepResponse.IncorrectCount, 0f);
                    float normalizedStepDifficulty = (float)Math.Log10(stepDifficulty + 1) / 10;
                    testDifficultySum += normalizedStepDifficulty;

                    var algoStepUpdateContent = new StringContent(JsonSerializer.Serialize(new
                    {
                        AlgoId = update.AlgoId,
                        Step = stepResult.AlgoStep,
                        Difficult = normalizedStepDifficulty
                    }), Encoding.UTF8, "application/json");

                    var algoStepUpdateResponse = await _httpClient.PutAsync($"{_testManagementUrl}/modify-algo-step/{update.AlgoId}/{stepResult.AlgoStep}", algoStepUpdateContent);
                    if (!algoStepUpdateResponse.IsSuccessStatusCode)
                    {
                        await LogError($"Failed to update algo step: algoId={update.AlgoId}, step={stepResult.AlgoStep}, status={algoStepUpdateResponse.StatusCode}");
                    }
                }

                var stepResponsesUpdateContent = new StringContent(JsonSerializer.Serialize(testStepResponses), Encoding.UTF8, "application/json");
                var stepResponsesUpdateResponse = await _httpClient.PutAsync($"{_testManagementUrl}/modify-step-responses/{update.TestId}", stepResponsesUpdateContent);
                if (!stepResponsesUpdateResponse.IsSuccessStatusCode)
                {
                    await LogError($"Failed to update test step responses: testId={update.TestId}, status={stepResponsesUpdateResponse.StatusCode}");
                }

                var testUpdateContent = new StringContent(JsonSerializer.Serialize(new
                {
                    TestId = update.TestId,
                    difficult = testDifficultySum
                }), Encoding.UTF8, "application/json");

                var testUpdateResponse = await _httpClient.PutAsync($"{_testManagementUrl}/modify-test/{update.TestId}", testUpdateContent);
                if (!testUpdateResponse.IsSuccessStatusCode)
                {
                    await LogError($"Failed to update test difficulty: testId={update.TestId}, status={testUpdateResponse.StatusCode}");
                }

                float abilitySum = 0f;
                int abilityCount = 0;
                foreach (var tsr in testStepResponses)
                {
                    if (tsr.IncorrectCount == 0) continue;
                    float stepAbility = (float)Math.Log((float)tsr.CorrectCount / tsr.IncorrectCount);
                    abilitySum += stepAbility;
                    abilityCount++;
                }

                float averageAbility = abilityCount > 0 ? abilitySum / abilityCount : 0f;
                float normalizedAbility = Math.Min(averageAbility, 0.95f);

                var userAbilities = await _usersDbContext.UserTestAbilities
                    .Where(uta => uta.TestId == update.TestId)
                    .ToListAsync();

                foreach (var userAbility in userAbilities)
                {
                    userAbility.Ability = normalizedAbility;
                    _usersDbContext.UserTestAbilities.Update(userAbility);
                }

                await _usersDbContext.SaveChangesAsync();
            }

            await LogSuccess($"Updated quality parameters for {updates.Count} tests");
            return Ok();
        }
        catch (Exception ex)
        {
            await LogError($"Exception in UploadQualityParameters: {ex.Message}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpGet("get-test-parameters/{testId}")]
    public async Task<IActionResult> GetTestParameters(int testId)
    {
        try
        {
            var testResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-test/{testId}");
            if (!testResponse.IsSuccessStatusCode)
            {
                await LogError($"Test not found: testId={testId}, status={testResponse.StatusCode}");
                return BadRequest($"Test not found: testId={testId}");
            }

            var test = await testResponse.Content.ReadFromJsonAsync<Test>();
            if (test == null)
            {
                await LogError($"Invalid test response: testId={testId}");
                return BadRequest("Invalid test response.");
            }

            var algoStepsResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-algo-steps/{test.AlgoId}");
            var algoStepsContent = await algoStepsResponse.Content.ReadAsStringAsync();
            var algoSteps = JsonSerializer.Deserialize<List<AlgoStep>>(algoStepsContent) ?? new List<AlgoStep>();
            var stepDifficulties = algoSteps
                .GroupBy(a => a.Step)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AlgoId).First().Difficult);

            var response = new
            {
                TestId = test.TestId,
                Difficulty = test.difficult,
                SolvedCount = test.SolvedCount,
                UnsolvedCount = test.UnsolvedCount,
                StepDifficulties = stepDifficulties
            };

            await LogSuccess($"Retrieved parameters for testId={testId}");
            return Ok(response);
        }
        catch (Exception ex)
        {
            await LogError($"Exception in GetTestParameters: {ex.Message}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpGet("get-statistics-for-test/{testId}/{userId}")]
    public async Task<IActionResult> GetStatisticsForTest(int testId, int userId)
    {
        try
        {
            var testResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-test/{testId}");
            if (!testResponse.IsSuccessStatusCode)
            {
                await LogError($"Test not found: testId={testId}, status={testResponse.StatusCode}");
                return BadRequest($"Test not found: testId={testId}");
            }

            var test = await testResponse.Content.ReadFromJsonAsync<Test>();
            if (test == null)
            {
                await LogError($"Invalid test response: testId={testId}");
                return BadRequest("Invalid test response.");
            }

            var userSolutionQuery = from s in _usersDbContext.SolutionsByUsers
                                    join v in _usersDbContext.VariablesSolutionsByUsers
                                    on new { s.UserStep, s.UserLineNumber, s.OrderNumber, s.TestId }
                                    equals new { v.UserStep, v.UserLineNumber, v.OrderNumber, v.TestId }
                                    where s.TestId == testId && s.UserId == userId
                                    select new { Solution = s, Variable = v };

            var userSolutionGroups = await userSolutionQuery
                .GroupBy(x => new { x.Solution.OrderNumber, x.Solution.UserStep, x.Solution.UserLineNumber, x.Solution.StepDifficult })
                .Select(g => new UserStepDto
                {
                    Step = g.Key.UserStep,
                    LineNumber = g.Key.UserLineNumber,
                    OrderNumber = g.Key.OrderNumber,
                    StepDifficult = g.Key.StepDifficult,
                    Variables = g.Select(x => new VariableDto
                    {
                        VarName = x.Variable.VarName,
                        VarValue = x.Variable.VarValue
                    }).ToList()
                })
                .ToListAsync();

            var userSolutionDto = new UserSolutionDto { Steps = userSolutionGroups };

            var programSolutionQuery = from s in _usersDbContext.SolutionsByPrograms
                                       join v in _usersDbContext.VariablesSolutionsByPrograms
                                       on new { s.ProgramStep, s.ProgramLineNumber, s.OrderNumber, s.TestId }
                                       equals new { v.ProgramStep, v.ProgramLineNumber, v.OrderNumber, v.TestId }
                                       where s.TestId == testId
                                       select new { Solution = s, Variable = v };

            var programSolutionGroups = await programSolutionQuery
                .GroupBy(x => new { x.Solution.OrderNumber, x.Solution.ProgramStep, x.Solution.ProgramLineNumber, x.Solution.StepDifficult })
                .Select(g => new ProgramStepDto
                {
                    Step = g.Key.ProgramStep,
                    LineNumber = g.Key.ProgramLineNumber,
                    OrderNumber = g.Key.OrderNumber,
                    StepDifficult = g.Key.StepDifficult,
                    Variables = g.Select(x => new VariableDto
                    {
                        VarName = x.Variable.VarName,
                        VarValue = x.Variable.VarValue
                    }).ToList()
                })
                .ToListAsync();

            var programSolutionDto = new ProgramSolutionDto { Steps = programSolutionGroups };

            float score = 0f;
            if (userSolutionGroups.Any())
            {
                var userSteps = userSolutionGroups.GroupBy(s => s.OrderNumber).ToList();
                var programSteps = programSolutionGroups.GroupBy(s => s.OrderNumber).ToList();
                int mismatchCount = 0;

                foreach (var pStep in programSteps)
                {
                    var matchingUserStep = userSteps.FirstOrDefault(u => u.All(uv => pStep.Any(pv =>
                        pv.Variables.Any(v => v.VarName == uv.Variables.FirstOrDefault()?.VarName && v.VarValue == uv.Variables.FirstOrDefault()?.VarValue))));
                    if (matchingUserStep == null)
                    {
                        mismatchCount++;
                    }
                    else
                    {
                        foreach (var pv in pStep.SelectMany(s => s.Variables))
                        {
                            var uv = matchingUserStep.SelectMany(s => s.Variables).FirstOrDefault(v => v.VarName == pv.VarName);
                            if (uv == null || uv.VarValue != pv.VarValue)
                            {
                                mismatchCount++;
                            }
                        }
                    }
                }

                mismatchCount += userSteps.Count() - programSteps.Count();

                int userStepCount = userSteps.Count();
                int programStepCount = programSteps.Count();
                float userAbility = await _usersDbContext.UserTestAbilities
                    .Where(uta => uta.UserId == userId && uta.TestId == testId)
                    .Select(uta => uta.Ability)
                    .FirstOrDefaultAsync();

                float rawScore = 100f * (1f - (float)mismatchCount / userStepCount) *
                                ((float)programStepCount / userStepCount) *
                                (1f - userAbility) *
                                (1f + test.difficult);

                score = Math.Max(0f, Math.Min(100f, rawScore));
            }

            var algoStepsResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-algo-steps/{test.AlgoId}");
            var algoStepsContent = await algoStepsResponse.Content.ReadAsStringAsync();
            var algoSteps = JsonSerializer.Deserialize<List<AlgoStep>>(algoStepsContent) ?? new List<AlgoStep>();
            var stepDifficulties = algoSteps
                .GroupBy(a => a.Step)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AlgoId).First().Difficult);

            var response = new StatsResponseDto
            {
                UserSolution = userSolutionDto,
                ProgramSolution = programSolutionDto
            };

            var result = new
            {
                Stats = response,
                Score = score,
                StepDifficulties = stepDifficulties
            };

            await LogSuccess($"Retrieved statistics for testId={testId}, userId={userId}");
            return Ok(result);
        }
        catch (Exception ex)
        {
            await LogError($"Exception in GetStatisticsForTest: {ex.Message}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    private async Task LogDebug(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.Now:MM/dd/yyyy HH:mm:ss}][TestQualityController] Debug: {message}\n");
    }

    private async Task LogError(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.Now:MM/dd/yyyy HH:mm:ss}][TestQualityController] Error: {message}\n");
    }

    private async Task LogSuccess(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.Now:MM/dd/yyyy HH:mm:ss}][TestQualityController] Success: {message}\n");
    }
}