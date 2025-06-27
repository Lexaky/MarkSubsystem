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
                if (Math.Abs(test.Difficult - userAbility) < 0.1f)
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
            if (updates == null || !updates.Any())
            {
                await LogDebug("No updates provided for quality parameters.");
                return Ok();
            }

            var sessionTestIds = new Dictionary<int, List<int>>();

            foreach (var update in updates)
            {
                await LogDebug($"Updating quality parameters for testId={update.TestId}, algoId={update.AlgoId}");

                // Получение теста
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

                // Получение всех шагов алгоритма
                var algoStepsResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-algo-steps/{update.AlgoId}");
                if (!algoStepsResponse.IsSuccessStatusCode)
                {
                    await LogError($"Failed to fetch algo steps: algoId={update.AlgoId}, status={algoStepsResponse.StatusCode}");
                    continue;
                }
                
                var algoStepsContent = await algoStepsResponse.Content.ReadAsStringAsync();
                await LogDebug($"Fetched algo steps for algoId={update.AlgoId}: {algoStepsContent}");
                List<AlgoStep> algoSteps;
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    algoSteps = JsonSerializer.Deserialize<List<AlgoStep>>(algoStepsContent, options) ?? new List<AlgoStep>();
                    if (!algoSteps.Any())
                    {
                        await LogError($"Deserialized algo steps is empty for algoId={update.AlgoId}");
                        continue;
                    }
                    foreach (var algoStep in algoSteps)
                    {
                        await LogDebug($"AlgoStep: algoId={algoStep.AlgoId}, step={algoStep.Step}, difficult={algoStep.Difficult}");
                    }
                }
                catch (JsonException ex)
                {
                    await LogError($"Failed to deserialize algo steps for algoId={update.AlgoId}: {ex.Message}");
                    continue;
                }

                // Получение всех ответов шагов для теста
                var stepResponsesResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-step-responses/{update.TestId}");
                var stepResponsesContent = await stepResponsesResponse.Content.ReadAsStringAsync();
                await LogDebug($"Fetched step responses for testId={update.TestId}: {stepResponsesContent}");
                List<TestStepResponse> testStepResponses;
                try
                {
                    testStepResponses = JsonSerializer.Deserialize<List<TestStepResponse>>(stepResponsesContent) ?? new List<TestStepResponse>();
                }
                catch (JsonException ex)
                {
                    await LogError($"Failed to deserialize step responses for testId={update.TestId}: {ex.Message}");
                    testStepResponses = new List<TestStepResponse>();
                }

                // Инициализация TestStepResponses для всех шагов из AlgoSteps
                foreach (var algoStep in algoSteps)
                {
                    if (!testStepResponses.Any(s => s.AlgoStep == algoStep.Step))
                    {
                        var newStepResponse = new TestStepResponse
                        {
                            TestId = update.TestId,
                            AlgoId = update.AlgoId,
                            AlgoStep = algoStep.Step,
                            CorrectCount = 0,
                            IncorrectCount = 0
                        };
                        testStepResponses.Add(newStepResponse);
                        await LogDebug($"Initialized step response: testId={update.TestId}, algoStep={algoStep.Step}, correct=0, incorrect=0");
                    }
                }

                // Обновление TestStepResponses из StepResults
                await LogDebug($"Processing {update.StepResults.Count} StepResults for testId={update.TestId}");
                foreach (var stepResult in update.StepResults)
                {
                    var matchingStep = algoSteps.FirstOrDefault(a => a.Step == stepResult.Step);
                    if (matchingStep == null)
                    {
                        await LogWarning($"Invalid step in StepResults: testId={update.TestId}, step={stepResult.Step}, not found in AlgoSteps for algoId={update.AlgoId}. Available steps: {string.Join(", ", algoSteps.Select(a => a.Step))}");
                        continue;
                    }

                    var stepResponse = testStepResponses.FirstOrDefault(s => s.TestId == update.TestId && s.AlgoStep == stepResult.Step);
                    if (stepResponse == null)
                    {
                        stepResponse = new TestStepResponse
                        {
                            TestId = update.TestId,
                            AlgoId = update.AlgoId,
                            AlgoStep = stepResult.Step,
                            CorrectCount = stepResult.IsCorrect ? 1 : 0,
                            IncorrectCount = stepResult.IsCorrect ? 0 : 1
                        };
                        testStepResponses.Add(stepResponse);
                        await LogDebug($"Added new step response: testId={update.TestId}, algoStep={stepResult.Step}, correct={stepResponse.CorrectCount}, incorrect={stepResponse.IncorrectCount}");
                    }
                    else
                    {
                        stepResponse.CorrectCount += stepResult.IsCorrect ? 1 : 0;
                        stepResponse.IncorrectCount += stepResult.IsCorrect ? 0 : 1;
                        await LogDebug($"Updated step response: testId={update.TestId}, algoStep={stepResult.Step}, correct={stepResponse.CorrectCount}, incorrect={stepResponse.IncorrectCount}");
                    }
                }

                // Сохранение TestStepResponses
                var stepResponsesContentStatus = new StringContent(JsonSerializer.Serialize(testStepResponses), Encoding.UTF8, "application/json");
                var stepResponsesUpdate = await _httpClient.PutAsync($"{_testManagementUrl}/modify-step-responses/{update.TestId}", stepResponsesContentStatus);
                if (!stepResponsesUpdate.IsSuccessStatusCode)
                {
                    var errorContent = await stepResponsesUpdate.Content.ReadAsStringAsync();
                    await LogError($"Failed to update test step responses: testId={update.TestId}, status={stepResponsesUpdate.StatusCode}, response={errorContent}");
                    continue;
                }
                await LogDebug($"Updated test step responses for testId={update.TestId}");

                // Повторная загрузка TestStepResponses для получения полных данных
                var allStepResponsesForTest = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-step-responses/{update.TestId}");
                if (!allStepResponsesForTest.IsSuccessStatusCode)
                {
                    var errorContent = await allStepResponsesForTest.Content.ReadAsStringAsync();
                    await LogError($"Failed to fetch all step responses for testId={update.TestId}, status={allStepResponsesForTest.StatusCode}, response={errorContent}");
                    continue;
                }

                var allStepResponsesContent = await allStepResponsesForTest.Content.ReadAsStringAsync();
                List<TestStepResponse> allTestStepResponsesForTest;
                try
                {
                    allTestStepResponsesForTest = JsonSerializer.Deserialize<List<TestStepResponse>>(allStepResponsesContent) ?? new List<TestStepResponse>();
                    await LogDebug($"Fetched all step responses for testId={update.TestId}: {allStepResponsesContent}");
                }
                catch (JsonException ex)
                {
                    await LogError($"Failed to deserialize all step responses for testId={update.TestId}: {ex.Message}");
                    allTestStepResponsesForTest = new List<TestStepResponse>();
                }

                // Расчёт сложности тестового задания
                int totalCorrectCount = allTestStepResponsesForTest.Sum(s => s.CorrectCount);
                int totalIncorrectCount = allTestStepResponsesForTest.Sum(s => s.IncorrectCount);
                int totalCount = totalCorrectCount + totalIncorrectCount;
                float testDifficulty;

                if (totalCount == 0)
                {
                    testDifficulty = totalCorrectCount > 0 ? (float)Math.Log(totalCorrectCount) : 0f;
                    await LogDebug($"Test difficulty calculation for testId={update.TestId}: totalCount=0, CorrectCount={totalCorrectCount}, testDifficulty={testDifficulty}");
                }
                else
                {
                    testDifficulty = (float)Math.Log((float)totalCorrectCount / totalCount);
                    await LogDebug($"Test difficulty calculation for testId={update.TestId}: CorrectCount={totalCorrectCount}, totalCount={totalCount}, testDifficulty={testDifficulty}");
                }

                float normalizedTestDifficulty = Math.Min(Math.Max(testDifficulty, 0f), 0.95f);
                await LogDebug($"Normalized test difficulty for testId={update.TestId}: {normalizedTestDifficulty}");

                float finalTestDifficulty = (normalizedTestDifficulty + test.Difficult) / 2f;
                await LogDebug($"Final test difficulty for testId={update.TestId}: current={test.Difficult}, new={normalizedTestDifficulty}, final={finalTestDifficulty}");

                var testContent = new StringContent(JsonSerializer.Serialize(new
                {
                    TestId = update.TestId,
                    difficult = finalTestDifficulty
                }), Encoding.UTF8, "application/json");

                var testResponseStatus = await _httpClient.PutAsync($"{_testManagementUrl}/modify-test/{update.TestId}", testContent);
                if (!testResponseStatus.IsSuccessStatusCode)
                {
                    var errorContent = await testResponseStatus.Content.ReadAsStringAsync();
                    await LogError($"Failed to update test difficulty: testId={update.TestId}, status={testResponseStatus.StatusCode}, response={errorContent}");
                    continue;
                }
                await LogDebug($"Updated test difficulty for testId={update.TestId}: {finalTestDifficulty}");

                // Расчёт сложности шагов алгоритма
                var stepDifficulties = new Dictionary<int, float>();
                foreach (var algoStep in algoSteps)
                {
                    var stepResponsesForAlgoStep = allTestStepResponsesForTest
                        .Where(s => s.AlgoId == update.AlgoId && s.AlgoStep == algoStep.Step)
                        .ToList();
                    int stepCorrectCount = stepResponsesForAlgoStep.Sum(s => s.CorrectCount);
                    int stepIncorrectCount = stepResponsesForAlgoStep.Sum(s => s.IncorrectCount);

                    await LogDebug($"Calculating difficulty for algoId={update.AlgoId}, step={algoStep.Step}: CorrectCount={stepCorrectCount}, IncorrectCount={stepIncorrectCount}");

                    float stepDifficulty;
                    if (stepCorrectCount == 0 && stepIncorrectCount == 0)
                    {
                        stepDifficulty = algoStep.Difficult != 0 ? algoStep.Difficult : 0f;
                        await LogDebug($"Step difficulty calculation for algoId={update.AlgoId}, step={algoStep.Step}: No data (CorrectCount=0, IncorrectCount=0), using existing difficulty={stepDifficulty}");
                    }
                    else if (stepCorrectCount == 0)
                    {
                        stepDifficulty = 0f;
                        await LogDebug($"Step difficulty calculation for algoId={update.AlgoId}, step={algoStep.Step}: CorrectCount=0, stepDifficulty=0");
                    }
                    else if (stepIncorrectCount > 0)
                    {
                        stepDifficulty = (float)Math.Log((float)stepCorrectCount / stepIncorrectCount);
                        await LogDebug($"Step difficulty calculation for algoId={update.AlgoId}, step={algoStep.Step}: CorrectCount={stepCorrectCount}, IncorrectCount={stepIncorrectCount}, stepDifficulty={stepDifficulty}");
                    }
                    else
                    {
                        stepDifficulty = (float)Math.Log(stepCorrectCount);
                        await LogDebug($"Step difficulty calculation for algoId={update.AlgoId}, step={algoStep.Step}: CorrectCount={stepCorrectCount}, IncorrectCount=0, stepDifficulty={stepDifficulty}");
                    }

                    float normalizedStepDifficulty = Math.Min(Math.Max(stepDifficulty, 0f), 1f);
                    stepDifficulties[algoStep.Step] = normalizedStepDifficulty;

                    var algoStepContent = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            AlgoId = update.AlgoId,
                            Step = algoStep.Step,
                            Difficult = normalizedStepDifficulty
                        }), Encoding.UTF8, "application/json");

                    var algoStepResponse = await _httpClient.PutAsync($"{_testManagementUrl}/modify-algo-step/{update.AlgoId}/{algoStep.Step}", algoStepContent);
                    if (!algoStepResponse.IsSuccessStatusCode)
                    {
                        var errorContent = await algoStepResponse.Content.ReadAsStringAsync();
                        await LogError($"Failed to update algo step: algoId={update.AlgoId}, step={algoStep.Step}, status={algoStepResponse.StatusCode}, response={errorContent}");
                    }
                    else
                    {
                        await LogDebug($"Updated algo step: algoId={update.AlgoId}, step={algoStep.Step}, difficulty={normalizedStepDifficulty}");
                    }
                }

                // Обновление StepDifficult в SolutionsByUsers
                var solutionsByUsers = await _usersDbContext.SolutionsByUsers
                    .Where(s => s.TestId == update.TestId)
                    .ToListAsync();
                foreach (var solution in solutionsByUsers)
                {
                    var matchingStep = algoSteps.FirstOrDefault(a => a.Step == solution.UserStep);
                    if (matchingStep == null)
                    {
                        await LogWarning($"Invalid UserStep in SolutionsByUsers: testId={update.TestId}, userStep={solution.UserStep}, not found in AlgoSteps for algoId={update.AlgoId}. Available steps: {string.Join(", ", algoSteps.Select(a => a.Step))}");
                        solution.StepDifficult = 0f;
                    }
                    else if (stepDifficulties.TryGetValue(solution.UserStep, out float difficulty))
                    {
                        solution.StepDifficult = difficulty;
                        await LogDebug($"Updated SolutionsByUsers: testId={update.TestId}, userStep={solution.UserStep}, StepDifficult={difficulty}");
                    }
                    else
                    {
                        solution.StepDifficult = 0f;
                        await LogWarning($"No difficulty found for UserStep={solution.UserStep} in testId={update.TestId}, setting StepDifficult=0");
                    }
                }
                _usersDbContext.SolutionsByUsers.UpdateRange(solutionsByUsers);

                await _usersDbContext.SaveChangesAsync();
                await LogDebug($"Updated StepDifficult for SolutionsByUsers for testId={update.TestId}");

                var sessionTests = await _usersDbContext.SessionTests
                    .Where(st => st.TestId == update.TestId)
                    .Select(st => st.SessionId)
                    .Distinct()
                    .ToListAsync();
                foreach (var sessionId in sessionTests)
                {
                    if (!sessionTestIds.ContainsKey(sessionId))
                    {
                        sessionTestIds[sessionId] = new List<int>();
                    }
                    sessionTestIds[sessionId].Add(update.TestId);
                }
            }

            // Обновление сложности сессий
            foreach (var sessionId in sessionTestIds.Keys)
            {
                var testIds = sessionTestIds[sessionId];
                float sessionDifficulty = 0f;
                int testCount = 0;

                foreach (var testId in testIds)
                {
                    var testResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-test/{testId}");
                    if (testResponse.IsSuccessStatusCode)
                    {
                        var test = await testResponse.Content.ReadFromJsonAsync<Test>();
                        if (test != null)
                        {
                            sessionDifficulty += test.Difficult;
                            testCount++;
                        }
                    }
                }

                if (testCount > 0)
                {
                    sessionDifficulty /= testCount;
                    var session = await _usersDbContext.Sessions.FirstOrDefaultAsync(s => s.SessionId == sessionId);
                    if (session != null)
                    {
                        session.Difficult = Math.Min(Math.Max(sessionDifficulty, 0f), 1f);
                        await LogDebug($"Updated session difficulty for sessionId={sessionId}: {sessionDifficulty}");
                        _usersDbContext.Sessions.Update(session);
                        await _usersDbContext.SaveChangesAsync();
                    }
                    else
                    {
                        await LogError($"Session not found: sessionId={sessionId}");
                    }
                }
            }

            await LogSuccess($"Updated quality parameters for {updates.Count} tests");
            return Ok();
        }
        catch (Exception ex)
        {
            await LogError($"Exception in UploadQualityParameters: {ex.Message}\nStackTrace: {ex.StackTrace}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
    private async Task LogWarning(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}][TestQualityController] Warning: {message}\n");
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
            if (!algoStepsResponse.IsSuccessStatusCode)
            {
                await LogError($"Failed to fetch algo steps: algoId={test.AlgoId}, status={algoStepsResponse.StatusCode}");
                return BadRequest($"Failed to fetch algo steps: algoId={test.AlgoId}");
            }

            var algoStepsContent = await algoStepsResponse.Content.ReadAsStringAsync();
            await LogDebug($"Fetched algo steps for algoId={test.AlgoId}: {algoStepsContent}");
            List<AlgoStep> algoSteps;
            try
            {
                algoSteps = JsonSerializer.Deserialize<List<AlgoStep>>(algoStepsContent) ?? new List<AlgoStep>();
                await LogDebug($"Deserialized algo steps for algoId={test.AlgoId}: {JsonSerializer.Serialize(algoSteps)}");
            }
            catch (JsonException ex)
            {
                await LogError($"Failed to deserialize algo steps for algoId={test.AlgoId}: {ex.Message}");
                algoSteps = new List<AlgoStep>();
            }

            var stepDifficulties = algoSteps
                .ToDictionary(a => a.Step, a => a.Difficult);

            var response = new
            {
                TestId = test.TestId,
                Difficulty = test.Difficult,
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
                    var matchingUserStep = userSteps.FirstOrDefault(u => u.Any(s => pStep.Any(pv =>
                        pv.Variables.Any(v => v.VarName == s.Variables.FirstOrDefault()?.VarName && v.VarValue == s.Variables.FirstOrDefault()?.VarValue))));
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

                mismatchCount += Math.Abs(userSteps.Count() - programSteps.Count());

                int userStepCount = userSteps.Count();
                int programStepCount = programSteps.Count();
                float userAbility = await _usersDbContext.UserTestAbilities
                    .Where(uta => uta.UserId == userId && uta.TestId == testId)
                    .Select(uta => uta.Ability)
                    .FirstOrDefaultAsync();

                float rawScore = 100f * (1f - (float)mismatchCount / userStepCount) *
                                ((float)programStepCount / userStepCount) *
                                (1f - userAbility) *
                                (1f + test.Difficult);

                score = Math.Max(0f, Math.Min(100f, rawScore));
            }

            var algoStepsResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-algo-steps/{test.AlgoId}");
            var algoStepsContent = await algoStepsResponse.Content.ReadAsStringAsync();
            await LogDebug($"Fetched algo steps for algoId={test.AlgoId}: {algoStepsContent}");
            List<AlgoStep> algoSteps;
            try
            {
                algoSteps = JsonSerializer.Deserialize<List<AlgoStep>>(algoStepsContent) ?? new List<AlgoStep>();
                await LogDebug($"Deserialized algo steps for algoId={test.AlgoId}: {JsonSerializer.Serialize(algoSteps)}");
            }
            catch (JsonException ex)
            {
                await LogError($"Failed to deserialize algo steps for algoId={test.AlgoId}: {ex.Message}");
                algoSteps = new List<AlgoStep>();
            }

            var stepDifficulties = algoSteps
                .ToDictionary(a => a.Step, a => a.Difficult);

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
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}][TestQualityController] Debug: {message}\n");
    }

    private async Task LogError(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}][TestQualityController] Error: {message}\n");
    }

    private async Task LogSuccess(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}][TestQualityController] Success: {message}\n");
    }
}

// Модельные классы
public class Test
{
    public int TestId { get; set; }
    public int AlgoId { get; set; }
    public float Difficult { get; set; }
    public int SolvedCount { get; set; }
    public int UnsolvedCount { get; set; }
}

public class AlgoStep
{
    public int AlgoId { get; set; }
    public int Step { get; set; }
    public float Difficult { get; set; }
}

public class TestStepResponse
{
    public int TestId { get; set; }
    public int AlgoId { get; set; }
    public int AlgoStep { get; set; }
    public int CorrectCount { get; set; }
    public int IncorrectCount { get; set; }
}

public class UpdateStepResponseDto
{
    public int TestId { get; set; }
    public int AlgoId { get; set; }
    public List<StepResultDto> StepResults { get; set; } = new List<StepResultDto>();
}

public class StepResultDto
{
    public int Step { get; set; }
    public bool IsCorrect { get; set; }
}

public class UserSolutionDto
{
    public List<UserStepDto> Steps { get; set; } = new List<UserStepDto>();
}

public class ProgramSolutionDto
{
    public List<ProgramStepDto> Steps { get; set; } = new List<ProgramStepDto>();
}

public class UserStepDto
{
    public int Step { get; set; }
    public int LineNumber { get; set; }
    public int OrderNumber { get; set; }
    public float StepDifficult { get; set; }
    public List<VariableDto> Variables { get; set; } = new List<VariableDto>();
}

public class ProgramStepDto
{
    public int Step { get; set; }
    public int LineNumber { get; set; }
    public int OrderNumber { get; set; }
    public float StepDifficult { get; set; }
    public List<VariableDto> Variables { get; set; } = new List<VariableDto>();
}

public class VariableDto
{
    public string VarName { get; set; } = string.Empty;
    public string VarValue { get; set; } = string.Empty;
}

public class StatsResponseDto
{
    public UserSolutionDto UserSolution { get; set; } = new UserSolutionDto();
    public ProgramSolutionDto ProgramSolution { get; set; } = new ProgramSolutionDto();
}