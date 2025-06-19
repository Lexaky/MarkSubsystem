using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MarkSubsystem.Data;
using MarkSubsystem.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MarkSubsystem.DTO;
using System.IO;

namespace MarkSubsystem.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EvaluationController : ControllerBase
{
    private readonly UsersDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly ILogger<EvaluationController> _logger;
    private readonly string _testManagementUrl = "http://InterpretatorService_2:8080/api/TestManagement";
    private readonly string _codeServiceUrl = "http://InterpretatorService_2:8080/api/Code";
    private readonly string _testQualityServiceUrl = "http://localhost:8080/api/TestQuality";
    private readonly string _debugLogPath = "/app/logs/debug.txt";

    public EvaluationController(UsersDbContext context, HttpClient httpClient, ILogger<EvaluationController> logger)
    {
        _context = context;
        _httpClient = httpClient;
        _logger = logger;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromBody] EvaluationRequestDto request)
    {
        try
        {
            await LogDebug($"Received request: {JsonSerializer.Serialize(request)}");

            var qualityUpdates = new List<UpdateStepResponseDto>();
            var response = new EvaluationResponseDto
            {
                Errors = request.Type == 0 ? new List<ErrorDto>() : null,
                Score = request.Type == 1 ? 0f : null
            };
            bool hasProcessedTests = false;
            float totalCorrectElements = 0f;
            float totalElements = 0f;

            foreach (var test in request.Tests)
            {
                var testId = test.TestId;
                var userId = request.UserId;
                var sessionId = request.SessionId;

                // Проверка существования теста
                await LogDebug($"Fetching test for testId={testId}");
                var testResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-test/{testId}");
                if (!testResponse.IsSuccessStatusCode)
                {
                    var errorMessage = $"Test not found: testId={testId}, status={testResponse.StatusCode}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }

                var testData = await testResponse.Content.ReadFromJsonAsync<Test>();
                if (testData == null)
                {
                    var errorMessage = $"Invalid test response: testId={testId}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }
                await LogDebug($"Test data: {JsonSerializer.Serialize(testData)}");

                hasProcessedTests = true;

                // Получение соответствия step и lineNumber
                await LogDebug($"Fetching variables for algoId={testData.AlgoId}");
                var algoVariablesResponse = await _httpClient.GetAsync($"{_codeServiceUrl}/getVariables/{testData.AlgoId}");
                if (!algoVariablesResponse.IsSuccessStatusCode)
                {
                    var errorMessage = $"Failed to fetch variables for algoId={testData.AlgoId}, status={algoVariablesResponse.StatusCode}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }

                var algoVariables = await algoVariablesResponse.Content.ReadFromJsonAsync<List<AlgoVariableDto>>();
                if (algoVariables == null)
                {
                    var errorMessage = $"Invalid variables response for algoId={testData.AlgoId}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }
                await LogDebug($"Algo variables: {JsonSerializer.Serialize(algoVariables)}");

                // Группировка переменных пользователя по sequence
                var sequences = test.Variables
                    .GroupBy(v => v.Sequence)
                    .Select(g => new { Sequence = g.Key, Variables = g.ToList() })
                    .OrderBy(g => g.Sequence)
                    .ToList();

                await LogDebug($"User sequences for testId={testId}: {string.Join(",", sequences.Select(s => s.Sequence))}");

                // Формирование текстового файла для substitute-values
                var fileContent = new StringBuilder();
                foreach (var sequence in sequences)
                {
                    var step = sequence.Variables.First().Step;
                    var algoVar = algoVariables.FirstOrDefault(av => av.Step == step);
                    if (algoVar == null)
                    {
                        var errorMessage = $"No matching lineNumber for testId={testId}, step={step}";
                        await LogError(errorMessage);
                        if (request.Type == 0)
                        {
                            response.Errors.Add(new ErrorDto
                            {
                                TestId = testId,
                                Sequence = sequence.Sequence,
                                Step = step,
                                Message = errorMessage
                            });
                        }
                        continue;
                    }

                    foreach (var variable in sequence.Variables)
                    {
                        fileContent.AppendLine($"{sequence.Sequence} {algoVar.LineNumber} {variable.VariableName} {variable.VariableValue}");
                    }
                }

                var fileContentString = fileContent.ToString();
                await LogDebug($"Substitute-values file content for testId={testId}:\n{fileContentString}");

                // Отправка файла в /api/Code/substitute-values/{codeId}/{testId}
                using var content = new MultipartFormDataContent();
                var fileStream = new MemoryStream(Encoding.UTF8.GetBytes(fileContentString));
                content.Add(new StreamContent(fileStream), "userDataFile", "input.txt");

                await LogDebug($"Sending POST to {_codeServiceUrl}/substitute-values/{testData.AlgoId}/{testId}");
                var substituteResponse = await _httpClient.PostAsync($"{_codeServiceUrl}/substitute-values/{testData.AlgoId}/{testId}", content);
                if (!substituteResponse.IsSuccessStatusCode)
                {
                    var errorContent = await substituteResponse.Content.ReadAsStringAsync();
                    var errorMessage = $"Failed to substitute values for testId={testId}: status={substituteResponse.StatusCode}, response={errorContent}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }

                var substituteResult = await substituteResponse.Content.ReadFromJsonAsync<SubstituteValuesResponseDto>();
                if (substituteResult == null || !substituteResult.CodeModel.IsSuccessful)
                {
                    var errorMessage = $"Substitute-values failed for testId={testId}: IsSuccessful={substituteResult?.CodeModel.IsSuccessful}, Mismatches={JsonSerializer.Serialize(substituteResult?.Mismatches)}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }
                await LogDebug($"Substitute-values response: {JsonSerializer.Serialize(substituteResult)}");

                // Сравнение пользовательских переменных с Values
                var stepResults = new List<StepResultDto>();
                bool hasWrongStep = false;
                int? wrongStepSequence = null;
                int programMaxStep = substituteResult.Values.Any() ? substituteResult.Values.Max(sv => sv.Step) : 0;
                int userMaxSequence = sequences.Any() ? sequences.Max(s => s.Sequence) : 0;

                foreach (var sequence in sequences)
                {
                    var step = sequence.Variables.First().Step;
                    bool isCorrect = true;
                    float correctElementsInSequence = 0f;
                    float totalElementsInSequence = 0f;

                    // Проверка корректности AlgoStep
                    if (!hasWrongStep)
                    {
                        // Проверяем, есть ли в algoVariables запись для данного step
                        var expectedAlgoVar = algoVariables.FirstOrDefault(av => av.Step == step && av.LineNumber == substituteResult.Values.FirstOrDefault(sv => sv.Step == sequence.Sequence && sv.VariableName == sequence.Variables.First().VariableName)?.TrackerHitId);
                        if (expectedAlgoVar == null)
                        {
                            hasWrongStep = true;
                            wrongStepSequence = sequence.Sequence;
                            isCorrect = false;
                            if (request.Type == 0)
                            {
                                response.Errors.Add(new ErrorDto
                                {
                                    TestId = testId,
                                    Sequence = sequence.Sequence,
                                    Step = step,
                                    Message = $"Неверный номер шага: нет записи в algoVariables для step={step} и sequence={sequence.Sequence}"
                                });
                            }
                            await LogDebug($"Wrong AlgoStep detected at sequence={sequence.Sequence}: no matching algoVariable for step={step}");
                        }
                    }

                    // Если уже обнаружена ошибка шага, текущий и последующие шаги считаются ошибочными
                    if (hasWrongStep)
                    {
                        isCorrect = false;
                        foreach (var userVar in sequence.Variables)
                        {
                            var substituteVar = substituteResult.Values.FirstOrDefault(sv =>
                                sv.Step == sequence.Sequence &&
                                String.Equals(sv.VariableName, userVar.VariableName, StringComparison.OrdinalIgnoreCase));

                            int elementCount = CountElements(userVar.VariableValue, substituteVar?.Type, substituteVar?.Rank ?? 0);
                            totalElementsInSequence += elementCount;
                            if (request.Type == 0)
                            {
                                response.Errors.Add(new ErrorDto
                                {
                                    TestId = testId,
                                    Sequence = sequence.Sequence,
                                    Step = step,
                                    VariableName = userVar.VariableName,
                                    Message = $"Переменная {userVar.VariableName} в шаге после неверного AlgoStep считается ошибочной"
                                });
                            }
                            await LogDebug($"Sequence={sequence.Sequence} marked as error due to wrong AlgoStep: variable={userVar.VariableName}, elements={elementCount}");
                        }
                    }
                    else
                    {
                        await LogDebug($"Comparing sequence={sequence.Sequence}, algoStep={step}");

                        foreach (var userVar in sequence.Variables.OrderBy(v => v.VariableName))
                        {
                            var substituteVar = substituteResult.Values.FirstOrDefault(sv =>
                                sv.Step == sequence.Sequence &&
                                String.Equals(sv.VariableName, userVar.VariableName, StringComparison.OrdinalIgnoreCase));

                            if (substituteVar == null)
                            {
                                isCorrect = false;
                                int elementCount = CountElements(userVar.VariableValue, "Unknown", 0);
                                totalElementsInSequence += elementCount;
                                if (request.Type == 0)
                                {
                                    response.Errors.Add(new ErrorDto
                                    {
                                        TestId = testId,
                                        Sequence = sequence.Sequence,
                                        Step = step,
                                        VariableName = userVar.VariableName,
                                        Message = $"No matching value in substitute-values for {userVar.VariableName} at sequence={sequence.Sequence}"
                                    });
                                }
                                await LogDebug($"No matching value for sequence={sequence.Sequence}, variable={userVar.VariableName}, elements={elementCount}");
                                continue;
                            }

                            var userValue = userVar.VariableValue?.Trim();
                            var substituteValue = substituteVar.Value?.Trim();
                            var userBytes = userValue != null ? BitConverter.ToString(Encoding.UTF8.GetBytes(userValue)).Replace("-", "") : "null";
                            var substituteBytes = substituteValue != null ? BitConverter.ToString(Encoding.UTF8.GetBytes(substituteValue)).Replace("-", "") : "null";

                            await LogDebug($"Comparing: sequence={sequence.Sequence}, algoStep={step}, userVar={userVar.VariableName}, userValue={userValue}, userBytes={userBytes}, substituteVar={substituteVar.VariableName}, substituteValue={substituteValue}, substituteBytes={substituteBytes}, substituteStep={substituteVar.Step}");

                            var (matches, total) = CompareValues(userValue, substituteValue, substituteVar.Type, substituteVar.Rank);
                            correctElementsInSequence += matches;
                            totalElementsInSequence += total;

                            if (matches < total)
                            {
                                isCorrect = false;
                                if (request.Type == 0)
                                {
                                    response.Errors.Add(new ErrorDto
                                    {
                                        TestId = testId,
                                        Sequence = sequence.Sequence,
                                        Step = step,
                                        VariableName = userVar.VariableName,
                                        Message = $"Incorrect value for {userVar.VariableName}: expected {substituteValue}, got {userValue}, mismatches={total - matches}"
                                    });
                                }
                                await LogDebug($"Mismatch: sequence={sequence.Sequence}, algoStep={step}, userVar={userVar.VariableName}, expected={substituteValue}, got={userValue}, userBytes={userBytes}, substituteBytes={substituteBytes}, mismatches={total - matches}");
                            }
                            else
                            {
                                await LogDebug($"Match: sequence={sequence.Sequence}, algoStep={step}, userVar={userVar.VariableName}, value={userValue}, bytes={userBytes}, elements={total}");
                            }
                        }
                    }

                    totalCorrectElements += correctElementsInSequence;
                    totalElements += totalElementsInSequence;

                    stepResults.Add(new StepResultDto
                    {
                        AlgoStep = step,
                        IsCorrect = isCorrect
                    });
                    await LogDebug($"Sequence={sequence.Sequence}, algoStep={step}, isCorrect={isCorrect}, correctElements={correctElementsInSequence}, totalElements={totalElementsInSequence}");
                }

                // Обработка лишних шагов пользователя
                if (userMaxSequence > programMaxStep)
                {
                    for (int seq = programMaxStep + 1; seq <= userMaxSequence; seq++)
                    {
                        var sequence = sequences.FirstOrDefault(s => s.Sequence == seq);
                        if (sequence == null) continue;

                        var step = sequence.Variables.First().Step;
                        float totalElementsInSequence = 0f;

                        foreach (var userVar in sequence.Variables)
                        {
                            int elementCount = CountElements(userVar.VariableValue, "Unknown", 0);
                            totalElementsInSequence += elementCount;
                            if (request.Type == 0)
                            {
                                response.Errors.Add(new ErrorDto
                                {
                                    TestId = testId,
                                    Sequence = sequence.Sequence,
                                    Step = step,
                                    VariableName = userVar.VariableName,
                                    Message = $"Лишний шаг sequence={sequence.Sequence}, переменная {userVar.VariableName} считается ошибочной"
                                });
                            }
                            await LogDebug($"Excess sequence={sequence.Sequence}, variable={userVar.VariableName}, elements={elementCount}");
                        }

                        totalElements += totalElementsInSequence;

                        stepResults.Add(new StepResultDto
                        {
                            AlgoStep = step,
                            IsCorrect = false
                        });
                        await LogDebug($"Excess sequence={sequence.Sequence}, algoStep={step}, isCorrect=false, totalElements={totalElementsInSequence}");
                    }
                }

                // Обработка недостающих шагов пользователя
                if (programMaxStep > userMaxSequence)
                {
                    for (int step = userMaxSequence + 1; step <= programMaxStep; step++)
                    {
                        var programVars = substituteResult.Values.Where(sv => sv.Step == step).ToList();
                        float totalElementsInSequence = 0f;

                        foreach (var programVar in programVars)
                        {
                            int elementCount = CountElements(programVar.Value, programVar.Type, programVar.Rank);
                            totalElementsInSequence += elementCount;
                            if (request.Type == 0)
                            {
                                response.Errors.Add(new ErrorDto
                                {
                                    TestId = testId,
                                    Sequence = step,
                                    Step = null,
                                    VariableName = programVar.VariableName,
                                    Message = $"Пропущен шаг sequence={step}, переменная {programVar.VariableName} считается ошибочной"
                                });
                            }
                            await LogDebug($"Missing sequence={step}, variable={programVar.VariableName}, elements={elementCount}");
                        }

                        totalElements += totalElementsInSequence;

                        stepResults.Add(new StepResultDto
                        {
                            AlgoStep = 0,
                            IsCorrect = false
                        });
                        await LogDebug($"Missing sequence={step}, algoStep=0, isCorrect=false, totalElements={totalElementsInSequence}");
                    }
                }

                // Сравнение количества шагов (для обучающей сессии)
                int userStepsCount = substituteResult.Meta.UserSteps?.Count ?? 0;
                int programStepsCount = substituteResult.Meta.ProgramSteps?.Count ?? 0;
                await LogDebug($"User steps count: {userStepsCount}, Program steps count: {programStepsCount}");

                if (userStepsCount > programStepsCount && request.Type == 0)
                {
                    response.Errors.Add(new ErrorDto
                    {
                        TestId = testId,
                        Message = "Пользователь имеет больше шагов, чем в программе"
                    });
                    await LogDebug("User has more steps than program");
                }
                else if (programStepsCount > userStepsCount && request.Type == 0)
                {
                    response.Errors.Add(new ErrorDto
                    {
                        TestId = testId,
                        Message = "Пользователь недописал решение"
                    });
                    await LogDebug("User has fewer steps than program");
                }

                // Сохранение решений пользователя
                var existingSolution = await _context.SolutionsByUsers
                    .AnyAsync(s => s.SessionId == sessionId && s.UserId == userId && s.TestId == testId);

                if (!existingSolution)
                {
                    foreach (var sequence in sequences)
                    {
                        var userStep = new SolutionsByUser
                        {
                            SessionId = sessionId,
                            UserId = userId,
                            TestId = testId,
                            UserStep = sequence.Variables.First().Step,
                            UserLineNumber = 1,
                            OrderNumber = sequence.Sequence,
                            StepDifficult = 0.5f
                        };
                        _context.SolutionsByUsers.Add(userStep);

                        foreach (var variable in sequence.Variables)
                        {
                            var userVariable = new VariablesSolutionsByUsers
                            {
                                UserStep = variable.Step,
                                UserLineNumber = 1,
                                OrderNumber = sequence.Sequence,
                                TestId = testId,
                                VarName = variable.VariableName,
                                VarValue = variable.VariableValue
                            };
                            _context.VariablesSolutionsByUsers.Add(userVariable);
                        }
                    }
                    await _context.SaveChangesAsync();
                    await LogDebug($"Saved solutions for testId={testId}, userId={userId}, sessionId={sessionId}");
                }
                else
                {
                    await LogDebug($"Solution already exists for sessionId={sessionId}, userId={userId}, testId={testId}. Skipping save.");
                }

                // Обновление TestStepResponses
                var updateStepResponse = new UpdateStepResponseDto
                {
                    TestId = testId,
                    AlgoId = testData.AlgoId,
                    StepResults = stepResults
                };

                var updateContent = new StringContent(
                    JsonSerializer.Serialize(updateStepResponse),
                    Encoding.UTF8,
                    "application/json");
                await LogDebug($"Sending to update-step-responses: {JsonSerializer.Serialize(updateStepResponse)}");

                var updateResponse = await _httpClient.PostAsync($"{_testManagementUrl}/update-step-responses", updateContent);
                if (!updateResponse.IsSuccessStatusCode)
                {
                    var errorContent = await updateResponse.Content.ReadAsStringAsync();
                    var errorMessage = $"Failed to update step responses: testId={testId}, status={updateResponse.StatusCode}, response={errorContent}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                }
                else
                {
                    await LogDebug($"Update-step-responses response: status={updateResponse.StatusCode}");
                }

                // Подготовка данных для TestQualityController
                qualityUpdates.Add(updateStepResponse);
            }

            // Вычисление общей оценки для контрольной сессии
            if (request.Type == 1 && totalElements > 0)
            {
                response.Score = (totalCorrectElements / totalElements) * 100f;
                await LogDebug($"Calculated score for control session: correctElements={totalCorrectElements}, totalElements={totalElements}, score={response.Score}");
            }

            // Вызов UploadQualityParameters
            if (qualityUpdates.Any())
            {
                var qualityContent = new StringContent(
                    JsonSerializer.Serialize(qualityUpdates),
                    Encoding.UTF8,
                    "application/json");
                await LogDebug($"Sending to upload-quality-parameters: {JsonSerializer.Serialize(qualityUpdates)}");

                var qualityResponse = await _httpClient.PostAsync($"{_testQualityServiceUrl}/upload-quality-parameters", qualityContent);
                if (!qualityResponse.IsSuccessStatusCode)
                {
                    var errorContent = await qualityResponse.Content.ReadAsStringAsync();
                    var errorMessage = $"Failed to update quality parameters: status={qualityResponse.StatusCode}, response={errorContent}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { Message = errorMessage });
                    }
                }
                else
                {
                    await LogDebug($"Upload-quality-parameters response: status={qualityResponse.StatusCode}");
                }
            }

            // Возвращаем 200 OK, если хотя бы один тест обработан, или 400, если все тесты не найдены
            if (!hasProcessedTests && request.Type == 0 && response.Errors.Any())
            {
                await LogError("No valid tests processed, returning errors.");
                return BadRequest(response);
            }

            await LogSuccess($"Uploaded evaluation data for userId={request.UserId}, sessionId={request.SessionId}");
            return Ok(response);
        }
        catch (Exception ex)
        {
            await LogError($"Exception in Upload: {ex.Message}, InnerException: {ex.InnerException?.Message}");
            return StatusCode(500, new EvaluationResponseDto
            {
                Errors = request.Type == 0 ? new List<ErrorDto> { new ErrorDto { Message = $"Internal server error: {ex.Message}" } } : null,
                Score = request.Type == 1 ? 0f : null
            });
        }
    }

    private (float matches, float total) CompareValues(string? userValue, string? substituteValue, string type, int rank)
    {
        float matches = 0f;
        float total = 0f;

        if (string.IsNullOrEmpty(userValue) || string.IsNullOrEmpty(substituteValue))
        {
            total = 1f;
            matches = string.Equals(userValue, substituteValue, StringComparison.Ordinal) ? 1f : 0f;
            return (matches, total);
        }

        if (rank == 0 || !type.Contains("[]"))
        {
            total = 1f;
            matches = String.Equals(userValue.Trim(), substituteValue.Trim(), StringComparison.Ordinal) ? 1f : 0f;
            return (matches, total);
        }
        else if (rank == 1 && type.Contains("[]"))
        {
            var userElements = userValue.Split(',', StringSplitOptions.TrimEntries);
            var substituteElements = substituteValue.Split(',', StringSplitOptions.TrimEntries);
            total = Math.Max(userElements.Length, substituteElements.Length);

            for (int i = 0; i < Math.Min(userElements.Length, substituteElements.Length); i++)
            {
                if (String.Equals(userElements[i].Trim(), substituteElements[i].Trim(), StringComparison.Ordinal))
                {
                    matches++;
                }
                else
                {
                    _logger.LogDebug($"Mismatch in array element at index {i}: expected {substituteElements[i]}, got {userElements[i]}");
                }
            }
            return (matches, total);
        }
        else if (rank == 2 && type.Contains("[]"))
        {
            var userRows = userValue.Split(';', StringSplitOptions.TrimEntries);
            var substituteRows = substituteValue.Split(';', StringSplitOptions.TrimEntries);
            total = 0f;
            matches = 0f;

            for (int i = 0; i < Math.Min(userRows.Length, substituteRows.Length); i++)
            {
                var userElements = userRows[i].Split(',', StringSplitOptions.TrimEntries);
                var substituteElements = substituteRows[i].Split(',', StringSplitOptions.TrimEntries);
                total += Math.Max(userElements.Length, substituteElements.Length);

                for (int j = 0; j < Math.Min(userElements.Length, substituteElements.Length); j++)
                {
                    if (String.Equals(userElements[j].Trim(), substituteElements[j].Trim(), StringComparison.Ordinal))
                    {
                        matches++;
                    }
                    else
                    {
                        _logger.LogDebug($"Mismatch in 2D array element at [{i},{j}]: expected {substituteElements[j]}, got {userElements[j]}");
                    }
                }
            }

            if (userRows.Length != substituteRows.Length)
            {
                for (int i = Math.Min(userRows.Length, substituteRows.Length); i < Math.Max(userRows.Length, substituteRows.Length); i++)
                {
                    var elements = (i < userRows.Length ? userRows[i] : substituteRows[i]).Split(',', StringSplitOptions.TrimEntries);
                    total += elements.Length;
                }
            }

            return (matches, total);
        }

        total = 1f;
        matches = String.Equals(userValue.Trim(), substituteValue.Trim(), StringComparison.Ordinal) ? 1f : 0f;
        return (matches, total);
    }

    private int CountElements(string? value, string type, int rank)
    {
        if (string.IsNullOrEmpty(value))
            return 1;

        if (rank == 0 || !type.Contains("[]"))
            return 1;

        if (rank == 1 && type.Contains("[]"))
            return value.Split(',', StringSplitOptions.TrimEntries).Length;

        if (rank == 2 && type.Contains("[]"))
        {
            var rows = value.Split(';', StringSplitOptions.TrimEntries);
            return rows.Sum(row => row.Split(',', StringSplitOptions.TrimEntries).Length);
        }

        return 1;
    }

    private async Task LogDebug(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.Now:MM/dd/yyyy HH:mm:ss}][EvaluationController] Debug: {message}\n");
    }

    private async Task LogError(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.Now:MM/dd/yyyy HH:mm:ss}][EvaluationController] Error: {message}\n");
    }

    private async Task LogSuccess(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.Now:MM/dd/yyyy HH:mm:ss}][EvaluationController] Success: {message}\n");
    }
}

// DTO для входного запроса
public class EvaluationRequestDto
{
    public int UserId { get; set; }
    public int SessionId { get; set; }
    public int Type { get; set; }
    public List<TestSubmissionDto> Tests { get; set; } = new List<TestSubmissionDto>();
}

public class TestSubmissionDto
{
    public int TestId { get; set; }
    public List<VariableSubmissionDto> Variables { get; set; } = new List<VariableSubmissionDto>();
}

public class VariableSubmissionDto
{
    public int Sequence { get; set; }
    public int Step { get; set; }
    public string VariableName { get; set; } = string.Empty;
    public string VariableValue { get; set; } = string.Empty;
}

// DTO для ответа
public class EvaluationResponseDto
{
    public List<ErrorDto>? Errors { get; set; }
    public float? Score { get; set; }
}

public class ErrorDto
{
    public int? TestId { get; set; }
    public int? Sequence { get; set; }
    public int? Step { get; set; }
    public string? VariableName { get; set; }
    public string Message { get; set; } = string.Empty;
}

// DTO для ответа от getVariables
public class AlgoVariableDto
{
    public int Sequence { get; set; }
    public int LineNumber { get; set; }
    public string VarName { get; set; } = string.Empty;
    public string VarType { get; set; } = string.Empty;
    public int Step { get; set; }
}

// DTO для ответа от substitute-values
public class SubstituteValuesResponseDto
{
    public CodeModelDto CodeModel { get; set; } = new CodeModelDto();
    public List<SubstituteValueDto> Values { get; set; } = new List<SubstituteValueDto>();
    public List<object> Mismatches { get; set; } = new List<object>();
    public MetaDto Meta { get; set; } = new MetaDto();
}

public class CodeModelDto
{
    public int CodeId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string StandardOutput { get; set; } = string.Empty;
    public string? ErrorOutput { get; set; }
    public string? WarningOutput { get; set; }
    public string OutputFilePath { get; set; } = string.Empty;
    public string ErrorFilePath { get; set; } = string.Empty;
    public string WarningFilePath { get; set; } = string.Empty;
    public bool IsSuccessful { get; set; }
}

public class SubstituteValueDto
{
    public int Step { get; set; }
    public int TrackerHitId { get; set; }
    public string VariableName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string Value { get; set; } = string.Empty;
}

public class MetaDto
{
    public List<object> UserSteps { get; set; } = new List<object>();
    public List<object> ProgramSteps { get; set; } = new List<object>();
}