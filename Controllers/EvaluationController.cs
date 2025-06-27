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
using Microsoft.EntityFrameworkCore.Metadata.Internal;

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
            var testScores = new List<float>();

            foreach (var test in request.Tests)
            {
                var testId = test.TestId;
                var userId = request.UserId;
                var sessionId = request.SessionId;

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
                var uniqueAlgoVariables = algoVariables
    .GroupBy(av => new { av.Step, av.LineNumber })
    .Select(g => g.First())
    .ToList();

                var stepToLineNumberMap = uniqueAlgoVariables.ToDictionary(av => av.Step, av => av.LineNumber);
                var lineNumberToStepMap = uniqueAlgoVariables.ToDictionary(av => av.LineNumber, av => av.Step);

                await LogDebug($"Fetching algo steps for algoId={testData.AlgoId}");
                var algoStepsResponse = await _httpClient.GetAsync($"{_testManagementUrl}/fetch-algo-steps/{testData.AlgoId}");
                if (!algoStepsResponse.IsSuccessStatusCode)
                {
                    var errorMessage = $"Failed to fetch algo steps for algoId={testData.AlgoId}, status={algoStepsResponse.StatusCode}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }
                var algoSteps = await algoStepsResponse.Content.ReadFromJsonAsync<List<AlgoStep>>();
                if (algoSteps == null)
                {
                    var errorMessage = $"Invalid algo steps response for algoId={testData.AlgoId}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }
                var stepDifficulties = algoSteps.ToDictionary(a => a.Step, a => a.Difficult);
                await LogDebug($"Fetched algo steps: {JsonSerializer.Serialize(stepDifficulties)}");

                float userAbility = await _context.UserTestAbilities
                    .Where(uta => uta.UserId == userId)
                    .AnyAsync()
                    ? await _context.UserTestAbilities
                        .Where(uta => uta.UserId == userId)
                        .AverageAsync(uta => uta.Ability)
                    : 0f;
                await LogDebug($"User ability for userId={userId}: {userAbility}");

                var sequences = test.Variables
                    .GroupBy(v => v.Sequence)
                    .Select(g => new { Sequence = g.Key, Variables = g.ToList() })
                    .OrderBy(g => g.Sequence)
                    .ToList();

                await LogDebug($"User sequences for testId={testId}: {string.Join(",", sequences.Select(s => s.Sequence))}");


                var validSequences = new List<(int Sequence, List<VariableSubmissionDto> Variables)>();
                foreach (var sequence in sequences)
                {
                    var step = sequence.Variables.First().Step;
                    var algoVar = algoVariables.FirstOrDefault(av => av.Step == step);
                    if (algoVar == null)
                    {
                        var errorMessage = $"Invalid step for testId={testId}, sequence={sequence.Sequence}, step={step}";
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
                    validSequences.Add((sequence.Sequence, sequence.Variables));
                    if (!stepToLineNumberMap.ContainsKey(step))
                    {
                        stepToLineNumberMap[step] = algoVar.LineNumber;
                    }
                }

                var fileContent = new StringBuilder();
                foreach (var (sequence, variables) in validSequences)
                {
                    var step = variables.First().Step;
                    var algoVar = algoVariables.First(av => av.Step == step);
                    foreach (var variable in variables)
                    {
                        fileContent.AppendLine($"{sequence} {algoVar.LineNumber} {variable.VariableName} {variable.VariableValue}");
                    }
                }

                var fileContentString = fileContent.ToString();
                await LogDebug($"Substitute-values file content for testId={testId}:\n{fileContentString}");

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

                using var trueContent = new MultipartFormDataContent();
                var trueFileStream = new MemoryStream(Encoding.UTF8.GetBytes("-"));
                trueContent.Add(new StreamContent(trueFileStream), "userDataFile", "input.txt");

                await LogDebug($"Sending POST to {_codeServiceUrl}/substitute-values/{testData.AlgoId}/{testId} for true program values");
                var trueSubstituteResponse = await _httpClient.PostAsync($"{_codeServiceUrl}/substitute-values/{testData.AlgoId}/{testId}", trueContent);
                if (!trueSubstituteResponse.IsSuccessStatusCode)
                {
                    var errorContent = await trueSubstituteResponse.Content.ReadAsStringAsync();
                    var errorMessage = $"Failed to fetch true program values for testId={testId}: status={trueSubstituteResponse.StatusCode}, response={errorContent}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }

                var trueSubstituteResult = await trueSubstituteResponse.Content.ReadFromJsonAsync<SubstituteValuesResponseDto>();
                if (trueSubstituteResult == null || !trueSubstituteResult.CodeModel.IsSuccessful)
                {
                    var errorMessage = $"True substitute-values failed for testId={testId}: IsSuccessful={trueSubstituteResult?.CodeModel.IsSuccessful}, Mismatches={JsonSerializer.Serialize(trueSubstituteResult?.Mismatches)}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                    }
                    continue;
                }
                await LogDebug($"True substitute-values response: {JsonSerializer.Serialize(trueSubstituteResult)}");

                var stepResults = new List<StepResultDto>();
                var testStepResponses = new List<TestStepResponse>();
                int mistakes = 0;
                int userAllVariablesCount = 0;

                int programMaxStep = trueSubstituteResult.Values.Any() ? trueSubstituteResult.Values.Max(sv => sv.Step) : 0;
                int userMaxSequence = validSequences.Any() ? validSequences.Max(s => s.Sequence) : 0;

                var trueProgramSequences = trueSubstituteResult.Values
                    .GroupBy(sv => sv.Step)
                    .Select(g => new { Sequence = g.Key, Variables = g.ToList() })
                    .OrderBy(g => g.Sequence)
                    .ToList();

                // Дополнительные проверки последовательности шагов
                var userSteps = validSequences.Select(s => s.Sequence).OrderBy(s => s).ToList();
                var programSteps = trueProgramSequences.Select(s => s.Sequence).OrderBy(s => s).ToList();
                await LogDebug($"User steps: [{string.Join(", ", userSteps)}]");
                await LogDebug($"Program steps: [{string.Join(", ", programSteps)}]");
                
                // Проверка неправильной последовательности
                bool sequenceError = false;
                int firstMismatchIndex = -1;
                for (int i = 0; i < Math.Min(userSteps.Count, programSteps.Count); i++)
                {
                    if (userSteps[i] != programSteps[i])
                    {
                        sequenceError = true;
                        firstMismatchIndex = i;
                        await LogDebug($"Sequence mismatch detected at index {i}: user step {userSteps[i]} vs program step {programSteps[i]}");
                        break;
                    }
                }
                
                // Если обнаружена неправильная последовательность, добавляем ошибки за все последующие шаги программы
                if (sequenceError && firstMismatchIndex >= 0)
                {
                    for (int i = firstMismatchIndex; i < programSteps.Count; i++)
                    {
                        var programSequence = trueProgramSequences.FirstOrDefault(ps => ps.Sequence == programSteps[i]);
                        if (programSequence != null)
                        {
                            int sequenceElementsCount = 0;
                            foreach (var programVar in programSequence.Variables)
                            {
                                int elementCount = CountElements(programVar.Value, programVar.Type, programVar.Rank);
                                sequenceElementsCount += elementCount;
                            }
                            mistakes += sequenceElementsCount;
                            await LogDebug($">>> ADDING {sequenceElementsCount} MISTAKES for sequence error at program step {programSteps[i]}");
                        }
                    }
                }
                
                // Проверка неправильного номера шага
                for (int i = 0; i < userSteps.Count; i++)
                {
                    bool stepMismatch = false;
                    
                    // Проверяем, есть ли пользовательский шаг в программе на правильной позиции
                    if (i < programSteps.Count && userSteps[i] != programSteps[i])
                    {
                        stepMismatch = true;
                    }
                    // Проверяем, есть ли пользовательский шаг в программе вообще
                    else if (i >= programSteps.Count || !programSteps.Contains(userSteps[i]))
                    {
                        stepMismatch = true;
                    }
                    
                    if (stepMismatch)
                    {
                        // Добавляем элементы всех переменных из этого и последующих шагов программы
                        for (int j = i; j < programSteps.Count; j++)
                        {
                            var programSequence = trueProgramSequences.FirstOrDefault(ps => ps.Sequence == programSteps[j]);
                            if (programSequence != null)
                            {
                                int sequenceElementsCount = 0;
                                foreach (var programVar in programSequence.Variables)
                                {
                                    int elementCount = CountElements(programVar.Value, programVar.Type, programVar.Rank);
                                    sequenceElementsCount += elementCount;
                                }
                                mistakes += sequenceElementsCount;
                                await LogDebug($">>> ADDING {sequenceElementsCount} MISTAKES for wrong step number at program step {programSteps[j]}");
                            }
                        }
                        break; // Выходим после первого несоответствия
                    }
                }
                
                // Проверка недорешенной задачи
                if (userSteps.Count < programSteps.Count)
                {
                    for (int i = userSteps.Count; i < programSteps.Count; i++)
                    {
                        var programSequence = trueProgramSequences.FirstOrDefault(ps => ps.Sequence == programSteps[i]);
                        if (programSequence != null)
                        {
                            int sequenceElementsCount = 0;
                            foreach (var programVar in programSequence.Variables)
                            {
                                int elementCount = CountElements(programVar.Value, programVar.Type, programVar.Rank);
                                sequenceElementsCount += elementCount;
                            }
                            mistakes += sequenceElementsCount;
                            await LogDebug($">>> ADDING {sequenceElementsCount} MISTAKES for unsolved step {programSteps[i]}");
                        }
                    }
                }
                
                // Проверка лишних шагов пользователя
                if (userSteps.Count > programSteps.Count)
                {
                    for (int i = programSteps.Count; i < userSteps.Count; i++)
                    {
                        var userSequence = validSequences.FirstOrDefault(us => us.Sequence == userSteps[i]);
                        if (userSequence.Variables != null)
                        {
                            int sequenceElementsCount = 0;
                            foreach (var userVar in userSequence.Variables)
                            {
                                int elementCount = CountElements(userVar.VariableValue, "Unknown", 0);
                                sequenceElementsCount += elementCount;
                            }
                            mistakes += sequenceElementsCount;
                            await LogDebug($">>> ADDING {sequenceElementsCount} MISTAKES for extra user step {userSteps[i]}");
                        }
                    }
                }

                // Проверяем шаги и переменные, собирая все ошибки
                foreach (var (sequence, variables) in validSequences)
                {
                    var userStep = variables.First().Step;
                    bool isCorrect = true;
                    float correctElementsInSequence = 0f;
                    float totalElementsInSequence = 0f;

                    // Проверка соответствия шага
                    var trueProgramSequence = trueProgramSequences.FirstOrDefault(tps => tps.Sequence == sequence);
                    if (trueProgramSequence == null)
                    {
                        isCorrect = false;
                        // Считаем все элементы пользовательских переменных как ошибки
                        int stepElementsCount = 0;
                        foreach (var userVar in variables)
                        {
                            int elementCount = CountElements(userVar.VariableValue, "Unknown", 0);
                            stepElementsCount += elementCount;
                        }
                        mistakes += stepElementsCount;
                        await LogDebug($">>> ADDING {stepElementsCount} MISTAKES for missing step {userStep} at sequence {sequence}");
                        
                        if (request.Type == 0)
                        {
                            response.Errors.Add(new ErrorDto
                            {
                                TestId = testId,
                                Sequence = sequence,
                                Step = userStep,
                                Message = $"User step {userStep} at sequence {sequence} does not exist in program execution"
                            });
                        }
                        await LogDebug($"Step mismatch at sequence={sequence}: user step={userStep} not found in true program steps");
                    }
                    else
                    {
                        // Проверка переменных для соответствия шага
                        bool stepMismatch = false;
                        foreach (var userVar in variables)
                        {
                            var trueVar = trueProgramSequence.Variables.FirstOrDefault(tv =>
                                String.Equals(tv.VariableName, userVar.VariableName, StringComparison.OrdinalIgnoreCase));
                            if (trueVar == null)
                            {
                                stepMismatch = true;
                                break;
                            }
                        }
                        if (stepMismatch)
                        {
                            isCorrect = false;
                            // Считаем все элементы переменных как ошибки при несоответствии переменных
                            int stepElementsCount = 0;
                            foreach (var userVar in variables)
                            {
                                int elementCount = CountElements(userVar.VariableValue, "Unknown", 0);
                                stepElementsCount += elementCount;
                            }
                            mistakes += stepElementsCount;
                            await LogDebug($">>> ADDING {stepElementsCount} MISTAKES for step variable mismatch at sequence {sequence}");
                            
                            if (request.Type == 0)
                            {
                                response.Errors.Add(new ErrorDto
                                {
                                    TestId = testId,
                                    Sequence = sequence,
                                    Step = userStep,
                                    Message = $"User step {userStep} at sequence {sequence} contains variables not matching true program step"
                                });
                            }
                            await LogDebug($"Step mismatch at sequence={sequence}: user step={userStep} variables do not match true program step");
                        }
                    }

                    // Проверка значений переменных
                    foreach (var userVar in variables.OrderBy(v => v.VariableName))
                    {
                        var substituteVar = substituteResult.Values.FirstOrDefault(sv =>
                            sv.Step == sequence &&
                            String.Equals(sv.VariableName, userVar.VariableName, StringComparison.OrdinalIgnoreCase));

                        int elementCount = CountElements(userVar.VariableValue, substituteVar?.Type ?? "Unknown", substituteVar?.Rank ?? 0);
                        userAllVariablesCount += elementCount;
                        totalElementsInSequence += elementCount;

                        if (substituteVar == null)
                        {
                            isCorrect = false;
                            mistakes += elementCount; // Считаем все элементы переменной как ошибки
                            await LogDebug($">>> ADDING {elementCount} MISTAKES for missing substitute value for {userVar.VariableName}");
                            
                            if (request.Type == 0)
                            {
                                response.Errors.Add(new ErrorDto
                                {
                                    TestId = testId,
                                    Sequence = sequence,
                                    Step = userStep,
                                    VariableName = userVar.VariableName,
                                    Message = $"No matching value in substitute-values for {userVar.VariableName} at sequence={sequence}"
                                });
                            }
                            await LogDebug($"No matching value for sequence={sequence}, variable={userVar.VariableName}, elements={elementCount}");
                            continue;
                        }

                        var userValue = userVar.VariableValue?.Trim();
                        var substituteValue = substituteVar.Value?.Trim();
                        var userBytes = userValue != null ? BitConverter.ToString(Encoding.UTF8.GetBytes(userValue)).Replace("-", "") : "null";
                        var substituteBytes = substituteValue != null ? BitConverter.ToString(Encoding.UTF8.GetBytes(substituteValue)).Replace("-", "") : "null";

                        await LogDebug($"Comparing: sequence={sequence}, algoStep={userStep}, userVar={userVar.VariableName}, userValue={userValue}, userBytes={userBytes}, substituteVar={substituteVar.VariableName}, substituteValue={substituteValue}, substituteBytes={substituteBytes}, substituteStep={substituteVar.Step}");

                        var (matches, total) = CompareValues(userValue, substituteValue, substituteVar.Type, substituteVar.Rank);
                        correctElementsInSequence += matches;
                        totalElementsInSequence += total;

                        if (matches < total)
                        {
                            isCorrect = false;
                            int elementMistakes = (int)(total - matches);
                            mistakes += elementMistakes;
                            await LogDebug($">>> ADDING {elementMistakes} MISTAKES for variable {userVar.VariableName} (matches: {matches}, total: {total})");
                            
                            if (request.Type == 0)
                            {
                                response.Errors.Add(new ErrorDto
                                {
                                    TestId = testId,
                                    Sequence = sequence,
                                    Step = userStep,
                                    VariableName = userVar.VariableName,
                                    Message = $"Incorrect value for {userVar.VariableName}: expected {substituteValue}, got {userValue}, mismatches={elementMistakes}"
                                });
                            }
                            await LogDebug($"Mismatch: sequence={sequence}, algoStep={userStep}, userVar={userVar.VariableName}, expected={substituteValue}, got={userValue}, userBytes={userBytes}, substituteBytes={substituteBytes}, mismatches={elementMistakes}");
                        }
                        else
                        {
                            await LogDebug($"Match: sequence={sequence}, algoStep={userStep}, userVar={userVar.VariableName}, value={userValue}, bytes={userBytes}, elements={total}");
                        }
                    }

                    // Проверка algoVariables
                    var expectedAlgoVar = algoVariables.FirstOrDefault(av => av.Step == userStep && av.LineNumber == substituteResult.Values.FirstOrDefault(sv => sv.Step == sequence && sv.VariableName == variables.First().VariableName)?.TrackerHitId);
                    if (expectedAlgoVar == null)
                    {
                        isCorrect = false;
                        // Для неверного номера шага добавляем 1 ошибку
                        mistakes += 1;
                        await LogDebug($">>> ADDING 1 MISTAKE for wrong AlgoStep at sequence {sequence}, step {userStep}");
                        
                        if (request.Type == 0)
                        {
                            response.Errors.Add(new ErrorDto
                            {
                                TestId = testId,
                                Sequence = sequence,
                                Step = userStep,
                                Message = $"Неверный номер шага: нет записи в algoVariables для step={userStep} и sequence={sequence}"
                            });
                        }
                        await LogDebug($"Wrong AlgoStep detected at sequence={sequence}: no matching algoVariable for step={userStep}");
                    }

                    stepResults.Add(new StepResultDto
                    {
                        Step = userStep,
                        IsCorrect = isCorrect
                    });

                    // Находим или создаем запись для данного шага
                    var existingStepResponse = testStepResponses.FirstOrDefault(tsr => 
                        tsr.TestId == testId && tsr.AlgoId == testData.AlgoId && tsr.AlgoStep == userStep);
                    
                    if (existingStepResponse == null)
                    {
                        testStepResponses.Add(new TestStepResponse
                        {
                            TestId = testId,
                            AlgoId = testData.AlgoId,
                            AlgoStep = userStep,
                            CorrectCount = isCorrect ? 1 : 0,
                            IncorrectCount = isCorrect ? 0 : 1
                        });
                    }
                    else
                    {
                        existingStepResponse.CorrectCount += isCorrect ? 1 : 0;
                        existingStepResponse.IncorrectCount += isCorrect ? 0 : 1;
                    }

                    await LogDebug($"Sequence={sequence}, algoStep={userStep}, isCorrect={isCorrect}, correctElements={correctElementsInSequence}, totalElements={totalElementsInSequence}");
                }

                if (userMaxSequence > programMaxStep)
                {
                    for (int sequence = programMaxStep + 1; sequence <= userMaxSequence; sequence++)
                    {
                        var userSequence = validSequences.FirstOrDefault(s => s.Sequence == sequence);
                        if (userSequence.Variables != null)
                        {
                            var step = userSequence.Variables.First().Step;
                            if (request.Type == 0)
                            {
                                response.Errors.Add(new ErrorDto
                                {
                                    TestId = testId,
                                    Sequence = sequence,
                                    Step = step,
                                    Message = $"Пользователь имеет лишний шаг sequence={sequence}"
                                });
                            }
                            // Для лишнего шага считаем все элементы переменных как ошибки
                            int stepElementsCount = 0;
                            foreach (var userVar in userSequence.Variables)
                            {
                                int elementCount = CountElements(userVar.VariableValue, "Unknown", 0);
                                stepElementsCount += elementCount;
                            }
                            mistakes += stepElementsCount;
                            await LogDebug($">>> ADDING {stepElementsCount} MISTAKES for excess step at sequence {sequence}");
                            await LogDebug($"User has excess step at sequence={sequence}");
                        }
                    }
                }

                if (programMaxStep > userMaxSequence)
                {
                    for (int step = userMaxSequence + 1; step <= programMaxStep; step++)
                    {
                        var programVars = trueSubstituteResult.Values.Where(sv => sv.Step == step).ToList();
                        float totalElementsInSequence = 0f;

                        foreach (var programVar in programVars)
                        {
                            int elementCount = CountElements(programVar.Value, programVar.Type, programVar.Rank);
                            totalElementsInSequence += elementCount;
                            mistakes += elementCount;
                            await LogDebug($">>> ADDING {elementCount} MISTAKES for missing sequence {step}, variable {programVar.VariableName}");
                            await LogDebug($"Missing sequence={step}, variable={programVar.VariableName}, elements={elementCount}");
                        }

                        stepResults.Add(new StepResultDto
                        {
                            Step = lineNumberToStepMap[programVars.ElementAt(step).TrackerHitId],
                            IsCorrect = false
                        });

                        testStepResponses.Add(new TestStepResponse
                        {
                            TestId = testId,
                            AlgoId = testData.AlgoId,
                            AlgoStep = lineNumberToStepMap[programVars.ElementAt(step).TrackerHitId],
                            CorrectCount = 0,
                            IncorrectCount = 1
                        });

                        if (request.Type == 0)
                        {
                            response.Errors.Add(new ErrorDto
                            {
                                TestId = testId,
                                Sequence = step,
                                Message = $"Пользователь пропустил шаг sequence={step}"
                            });
                        }
                        await LogDebug($"Missing sequence={step}, algoStep=0, isCorrect=false, totalElements={totalElementsInSequence}");
                    }
                }

                float testScore = 0f;
                int userStepsCount = validSequences.Count;
                int programStepsCount = programMaxStep;
                if (userStepsCount > 0)
                {
                    float rawScore = 100f * (1f - (float)mistakes / userAllVariablesCount) *
                                     ((float)programStepsCount / userStepsCount);
                    testScore = Math.Max(0f, Math.Min(100f, rawScore));
                    testScores.Add(testScore);
                    
                    // Подробное логирование для отладки
                    await LogDebug($"=== SCORE CALCULATION FOR TEST {testId} ===");
                    await LogDebug($"Mistakes: {mistakes}");
                    await LogDebug($"User all variables count: {userAllVariablesCount}");
                    await LogDebug($"User steps count: {userStepsCount}");
                    await LogDebug($"Program steps count: {programStepsCount}");
                    await LogDebug($"Error rate: {(float)mistakes / userAllVariablesCount:F4}");
                    await LogDebug($"Accuracy: {1f - (float)mistakes / userAllVariablesCount:F4}");
                    await LogDebug($"Step ratio: {(float)programStepsCount / userStepsCount:F4}");
                    await LogDebug($"Raw score: {rawScore:F2}");
                    await LogDebug($"Final score: {testScore:F2}");
                    await LogDebug($"=== END SCORE CALCULATION ===");
                }

                if (request.Type == 1)
                {
                    float correctTotal = testStepResponses.Sum(r => r.CorrectCount);
                    float incorrectTotal = testStepResponses.Sum(r => r.IncorrectCount);
                    float ability = 0f;
                    if (correctTotal > 0)
                    {
                        ability = incorrectTotal == 0
                            ? (float)Math.Log(correctTotal)
                            : (float)Math.Log(correctTotal / incorrectTotal);
                        ability = Math.Min(Math.Max(0f, ability), 0.95f);
                    }
                    await LogDebug($"Calculated ability for testId={testId}: correct={correctTotal}, incorrect={incorrectTotal}, ability={ability}");

                    var userTestAbility = await _context.UserTestAbilities
                        .FirstOrDefaultAsync(uta => uta.UserId == userId && uta.TestId == testId);

                    if (userTestAbility == null)
                    {
                        userTestAbility = new UserTestAbility
                        {
                            UserId = userId,
                            TestId = testId,
                            Ability = ability
                        };
                        _context.UserTestAbilities.Add(userTestAbility);
                        await LogDebug($"Added new ability: userId={userId}, testId={testId}, ability={ability}");
                    }
                    else
                    {
                        userTestAbility.Ability = (userTestAbility.Ability + ability) / 2f;
                        userTestAbility.Ability = Math.Min(userTestAbility.Ability, 0.95f);
                        _context.UserTestAbilities.Update(userTestAbility);
                        await LogDebug($"Updated ability: userId={userId}, testId={testId}, newAbility={userTestAbility.Ability}");
                    }
                }

                // Handle user and program solutions within a transaction
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Delete existing user solutions
                    var existingUserSolution = await _context.SolutionsByUsers
                        .Where(s => s.SessionId == sessionId && s.UserId == userId && s.TestId == testId)
                        .ToListAsync();
                    if (existingUserSolution.Any())
                    {
                        await LogDebug($"Removing existing user solution for sessionId={sessionId}, userId={userId}, testId={testId}");
                        var existingUserVariables = await _context.VariablesSolutionsByUsers
                            .Where(v => v.TestId == testId && v.UserId == userId && existingUserSolution.Select(s => s.OrderNumber).Contains(v.OrderNumber))
                            .ToListAsync();
                        _context.VariablesSolutionsByUsers.RemoveRange(existingUserVariables);
                        _context.SolutionsByUsers.RemoveRange(existingUserSolution);
                        await _context.SaveChangesAsync();
                        await LogDebug($"Removed {existingUserSolution.Count()} user solutions and {existingUserVariables.Count()} variables");
                    }

                    // Add new user solutions
                    foreach (var (sequence, variables) in validSequences)
                    {
                        var step = variables.First().Step;
                        var stepDifficulty = stepDifficulties.GetValueOrDefault(step, 0.5f);
                        var userStep = new SolutionsByUser
                        {
                            SessionId = sessionId,
                            UserId = userId,
                            TestId = testId,
                            UserStep = step,
                            UserLineNumber = 1,
                            OrderNumber = sequence,
                            StepDifficult = stepDifficulty
                        };
                        _context.SolutionsByUsers.Add(userStep);
                        var variableGroups = variables.GroupBy(v => v.VariableName).ToList(); //new

                        //foreach (var variable in variables)
                        foreach (var variableGroup in variableGroups) //new
                        {
                            var variable = variableGroup.Last(); //new
                            var userVariable = new VariablesSolutionsByUsers
                            {
                                UserStep = step,
                                UserLineNumber = 1,
                                OrderNumber = sequence,
                                TestId = testId,
                                VarName = variable.VariableName,
                                VarValue = variable.VariableValue,
                                UserId = userId
                            };
                            _context.VariablesSolutionsByUsers.Add(userVariable);
                        }
                    }

                    // Check for duplicate variables in trueProgramSequences
                    var duplicateProgramVariables = trueProgramSequences
                        .SelectMany(s => s.Variables, (s, v) => new { s.Sequence, v.VariableName })
                        .GroupBy(x => new { x.Sequence, x.VariableName })
                        .Where(g => g.Count() > 1)
                        .ToList();

                    if (duplicateProgramVariables.Any())
                    {
                        var errorMessage = $"Duplicate variables detected in trueProgramSequences for testId={testId}: {JsonSerializer.Serialize(duplicateProgramVariables)}";
                        await LogError(errorMessage);
                        response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                        await transaction.RollbackAsync();
                        continue;
                    }

                    // Delete existing program solutions
                    var existingProgramSolution = await _context.SolutionsByPrograms
                        .Where(s => s.SessionId == sessionId && s.TestId == testId)
                        .ToListAsync();
                    if (existingProgramSolution.Any())
                    {
                        await LogDebug($"Removing existing program solution for sessionId={sessionId}, testId={testId}");
                        var existingProgramVariables = await _context.VariablesSolutionsByPrograms
                            .Where(v => v.TestId == testId && existingProgramSolution.Select(s => s.OrderNumber).Contains(v.OrderNumber))
                            .ToListAsync();
                        _context.VariablesSolutionsByPrograms.RemoveRange(existingProgramVariables);
                        _context.SolutionsByPrograms.RemoveRange(existingProgramSolution);
                        await _context.SaveChangesAsync();
                        await LogDebug($"Removed {existingProgramSolution.Count()} program solutions and {existingProgramVariables.Count()} variables");
                    }

                    // Add new program solutions
                    foreach (var sequence in trueProgramSequences)
                    {
                        var step = sequence.Sequence;
                        var stepDifficulty = stepDifficulties.GetValueOrDefault(step, 0.5f);
                        var programStep = new SolutionsByProgram
                        {
                            SessionId = sessionId,
                            TestId = testId,
                            ProgramStep = step,
                            ProgramLineNumber = 1,
                            OrderNumber = step,
                            StepDifficult = stepDifficulty
                        };
                        _context.SolutionsByPrograms.Add(programStep);

                        //foreach (var variable in sequence.Variables)
                        var variableGroups = sequence.Variables.GroupBy(v => v.VariableName).ToList(); //new
                        foreach (var variableGroup in variableGroups) //new
                        {
                            var variable = variableGroup.Last(); //new
                            var programVariable = new VariablesSolutionsByProgram
                            {
                                ProgramStep = step,
                                ProgramLineNumber = 1,
                                OrderNumber = step,
                                TestId = testId,
                                VarName = variable.VariableName,
                                VarValue = variable.Value
                            };
                            _context.VariablesSolutionsByPrograms.Add(programVariable);
                        }
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    await LogDebug($"Saved solutions for testId={testId}, userId={userId}, sessionId={sessionId}");
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    await LogError($"Exception in saving solutions: {ex.Message}, InnerException: {ex.InnerException?.Message}");
                    response.Errors.Add(new ErrorDto
                    {
                        TestId = testId,
                        Message = $"Failed to save solutions: {ex.Message}, InnerException: {ex.InnerException?.Message}"
                    });
                    continue; // Continue to the next test to avoid breaking the entire loop
                }


                var updateStepResponse = new UpdateStepResponseDto
                {
                    TestId = testId,
                    AlgoId = testData.AlgoId,
                    StepResults = stepResults
                };

                if (request.Type == 1)
                {
                    var stepResponsesUpdateContent = new StringContent(
                        JsonSerializer.Serialize(testStepResponses),
                        Encoding.UTF8,
                        "application/json");
                    await LogDebug($"Sending to modify-step-responses: {JsonSerializer.Serialize(testStepResponses)}");

                    var updateResponse = await _httpClient.PutAsync($"{_testManagementUrl}/modify-step-responses/{testId}", stepResponsesUpdateContent);
                    if (!updateResponse.IsSuccessStatusCode)
                    {
                        var errorContent = await updateResponse.Content.ReadAsStringAsync();
                        var errorMessage = $"Failed to modify step responses: testId={testId}, status={updateResponse.StatusCode}, response={errorContent}";
                        await LogError(errorMessage);
                        if (request.Type == 0)
                        {
                            response.Errors.Add(new ErrorDto { TestId = testId, Message = errorMessage });
                        }
                    }
                    else
                    {
                        await LogDebug($"Modify-step-responses response: status={updateResponse.StatusCode}");
                    }

                    qualityUpdates.Add(updateStepResponse);
                }
                else
                {
                    await LogDebug($"Training session (Type=0), skipping modify-step-responses for testId={testId}");
                }
            }

            if (testScores.Any())
            {
                float averageScore = testScores.Average();
                response.Score = averageScore;
                await LogDebug($"Average score for sessionId={request.SessionId}: {averageScore}");

                var existingGrade = await _context.Grades
                    .FirstOrDefaultAsync(g => g.UserId == request.UserId && g.SessionId == request.SessionId);

                if (existingGrade == null)
                {
                    var newGrade = new Grade
                    {
                        UserId = request.UserId,
                        SessionId = request.SessionId,
                        Mark = averageScore,
                        Datetime = DateTime.UtcNow
                    };
                    _context.Grades.Add(newGrade);
                    await LogDebug($"Added new grade: userId={request.UserId}, sessionId={request.SessionId}, mark={averageScore}");
                }
                else if (request.Type == 1)
                {
                    existingGrade.Mark = averageScore;
                    existingGrade.Datetime = DateTime.UtcNow;
                    _context.Grades.Update(existingGrade);
                    await LogDebug($"Updated grade: userId={request.UserId}, sessionId={request.SessionId}, mark={averageScore}");
                }
                else
                {
                    await LogDebug($"Training session (Type=0), grade not updated: userId={request.UserId}, sessionId={request.SessionId}, existing mark={existingGrade.Mark}");
                }

                await _context.SaveChangesAsync();
            }

            if (request.Type == 1 && qualityUpdates.Any())
            {
                var qualityContent = new StringContent(
                    JsonSerializer.Serialize(qualityUpdates),
                    Encoding.UTF8,
                    "application/json");
                await LogDebug($"Submitting to upload-quality parameters: {JsonSerializer.Serialize(qualityUpdates)}");

                var qualityResponse = await _httpClient.PostAsync($"{_testQualityServiceUrl}/upload-quality-parameters", qualityContent);
                if (!qualityResponse.IsSuccessStatusCode)
                {
                    var errorContent = await qualityResponse.Content.ReadAsStringAsync();
                    var errorMessage = $"Failed to update quality parameters: {errorContent}, status={qualityResponse.StatusCode}";
                    await LogError(errorMessage);
                    if (request.Type == 0)
                    {
                        response.Errors.Add(new ErrorDto() { Message = errorMessage });
                    }
                }
                else
                {
                    await LogDebug($"Upload-quality-parameters response: status={qualityResponse.StatusCode}");
                }
            }
            else if (request.Type == 1 && !qualityUpdates.Any())
            {
                await LogDebug($"No quality updates to send for sessionId={request.SessionId}");
            }

            if (!hasProcessedTests && request.Type == 0 && response.Errors.Any())
            {
                await LogError("No valid tests processed, returning errors.");
                return BadRequest(response);
            }

            await LogSuccess($"Successfully uploaded evaluation data for userId={request.UserId}, sessionId={request.SessionId}");
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


    [HttpGet("get-user-mark/{sessionId}/{userId}")]
    public async Task<IActionResult> GetUserMarkBySessionId(int sessionId, int userId)
    {
        try
        {
            await LogDebug($"Fetching mark for userId={userId}, sessionId={sessionId}");
            var grade = await _context.Grades
                .FirstOrDefaultAsync(g => g.UserId == userId && g.SessionId == sessionId);

            if (grade == null)
            {
                var errorMessage = $"No grade found for userId={userId}, sessionId={sessionId}";
                await LogError(errorMessage);
                return NotFound(errorMessage);
            }

            await LogDebug($"Found mark: userId={userId}, sessionId={sessionId}, mark={grade.Mark}");
            return Ok(new { Mark = grade.Mark });
        }
        catch (Exception ex)
        {
            await LogError($"Exception in GetUserMarkBySessionId: {ex.Message}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpGet("get-statistics/{sessionId}/{userId}")]
    public async Task<IActionResult> GetStatisticsForUser(int sessionId, int userId)
    {
        try
        {
            await LogDebug($"Fetching statistics for userId={userId}, sessionId={sessionId}");

            var solutions = await _context.SolutionsByUsers
                .Where(s => s.SessionId == sessionId && s.UserId == userId)
                .Join(_context.VariablesSolutionsByUsers,
                    s => new { s.TestId, s.OrderNumber, s.UserStep },
                    v => new { v.TestId, v.OrderNumber, v.UserStep },
                    (s, v) => new
                    {
                        s.TestId,
                        s.OrderNumber,
                        s.UserStep,
                        s.StepDifficult,
                        VariableName = v.VarName,
                        VariableValue = v.VarValue
                    })
                .GroupBy(x => new { x.TestId, x.OrderNumber, x.UserStep, x.StepDifficult })
                .Select(g => new UserSolutionNewDto
                {
                    TestId = g.Key.TestId,
                    Sequence = g.Key.OrderNumber,
                    Step = g.Key.UserStep,
                    StepDifficulty = g.Key.StepDifficult,
                    Variables = g.Select(v => new UserVariableDto
                    {
                        Name = v.VariableName,
                        Value = v.VariableValue
                    }).ToList()
                })
                .ToListAsync();

            if (!solutions.Any())
            {
                var errorMessage = $"No solutions found for userId={userId}, sessionId={sessionId}";
                await LogError(errorMessage);
                return NotFound(errorMessage);
            }

            await LogDebug($"Found {solutions.Count} solutions for userId={userId}, sessionId={sessionId}");
            return Ok(solutions);
        }
        catch (Exception ex)
        {
            await LogError($"Exception in GetStatisticsForUser: {ex.Message}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
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
            return value.Split(',', StringSplitOptions.None).Length;

        if (rank == 2 && type.Contains("[]"))
        {
            var rows = value.Split(';', StringSplitOptions.None);
            return rows.Sum(row => row.Split(',', StringSplitOptions.None).Length);
        }

        return 1;
    }

    private async Task LogDebug(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}][EvaluationController] Debug: {message}\n");
    }

    private async Task LogError(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}][EvaluationController] Error: {message}\n");
    }

    private async Task LogSuccess(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.UtcNow:MM/dd/yyyy HH:mm:ss}][EvaluationController] Success: {message}\n");
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

// DTO для статистики пользователя
public class UserSolutionNewDto
{
    public int TestId { get; set; }
    public int Sequence { get; set; }
    public int Step { get; set; }
    public float StepDifficulty { get; set; }
    public List<UserVariableDto> Variables { get; set; } = new List<UserVariableDto>();
}

public class UserVariableDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}