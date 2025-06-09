using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MarkSubsystem.Data;
using MarkSubsystem.Models;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarkSubsystem.DTO;
using System.Net.Http.Json;

namespace MarkSubsystem.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EvaluationController : ControllerBase
{
    private readonly UsersDbContext _usersDbContext;
    private readonly HttpClient _httpClient;
    private readonly string _substituteValuesUrl = "http://InterpretatorService_2:8080/api/Code/substitute-values";
    private readonly string _codeControllerUrl = "http://InterpretatorService_2:8080/api/Code";
    private readonly string _debugLogPath = "/app/logs/debug.txt";

    public EvaluationController(UsersDbContext usersDbContext, HttpClient httpClient)
    {
        _usersDbContext = usersDbContext;
        _httpClient = httpClient;
    }

    [HttpGet("check-users-db")]
    public async Task<IActionResult> CheckUsersDb()
    {
        try
        {
            int userCount = await _usersDbContext.Users.CountAsync();
            return Ok(new { Message = "Successfully connected to Users database", UserCount = userCount });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = "Failed to connect to Users database", Error = ex.Message });
        }
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromBody] UploadRequestDto request)
    {
        try
        {
            // Валидация входных данных
            if (request.Type != 0 && request.Type != 1)
            {
                await LogError($"Invalid session type: {request.Type}");
                return BadRequest("Type must be 0 or 1.");
            }

            // Проверка существования пользователя
            if (!await _usersDbContext.Users.AnyAsync(u => u.UserId == request.UserId))
            {
                await LogError($"User not found: userId={request.UserId}");
                return BadRequest("User not found.");
            }

            // Проверка существования сессии и тестов
            var session = await _usersDbContext.Sessions
                .Include(s => s.SessionTests)
                .FirstOrDefaultAsync(s => s.SessionId == request.SessionId);
            if (session == null)
            {
                await LogError($"Session not found: sessionId={request.SessionId}");
                return BadRequest("Session not found.");
            }

            var sessionTestIds = session.SessionTests.Select(st => st.TestId).ToHashSet();
            var requestTestIds = request.Tests.Select(t => t.TestId).ToHashSet();
            if (!requestTestIds.All(id => sessionTestIds.Contains(id)))
            {
                await LogError($"Some tests are not associated with session: {string.Join(",", requestTestIds.Except(sessionTestIds))}");
                return BadRequest("Some tests are not associated with the session.");
            }

            // Результаты для type=0
            var mismatches = new List<MismatchResponseDto>();
            // Счётчик несоответствий для type=1
            int totalMismatches = 0;
            // Словарь для хранения шагов программы по testId (из Values)
            var programStepsByTestId = new Dictionary<int, List<int>>();

            foreach (var testData in request.Tests)
            {
                // Получение теста через API
                var testResponse = await _httpClient.GetAsync($"{_codeControllerUrl}/tests/{testData.TestId}");
                if (!testResponse.IsSuccessStatusCode)
                {
                    await LogError($"Test not found: testId={testData.TestId}, status={testResponse.StatusCode}");
                    return BadRequest($"Test not found: testId={testData.TestId}");
                }

                var test = await testResponse.Content.ReadFromJsonAsync<TestDto>();
                var algoId = test.AlgoId;

                // Получение всех отслеживаемых переменных для algo_id
                var variablesResponse = await _httpClient.GetAsync($"{_codeControllerUrl}/getVariables/{algoId}");
                if (!variablesResponse.IsSuccessStatusCode)
                {
                    await LogError($"Failed to get variables: algoId={algoId}, status={variablesResponse.StatusCode}");
                    return StatusCode((int)variablesResponse.StatusCode, "Error fetching variables.");
                }

                var trackedVariables = await variablesResponse.Content.ReadFromJsonAsync<List<TrackedVariableDto>>();

                // Формирование текстового файла
                var userDataContent = new StringBuilder();
                var userSequences = testData.Variables.Select(v => v.Sequence).Distinct().ToList();
                await LogSuccess($"User sequences for testId={testData.TestId}: {string.Join(",", userSequences)}");
                foreach (var variable in testData.Variables)
                {
                    // Проверка переменной
                    var trackedVariable = trackedVariables.FirstOrDefault(v =>
                        v.Step == variable.Step && v.VarName == variable.VariableName);
                    if (trackedVariable == null)
                    {
                        await LogError($"Variable not found: testId={testData.TestId}, step={variable.Step}, name={variable.VariableName}");
                        return BadRequest($"Variable not found: {variable.VariableName} for step {variable.Step}");
                    }

                    // Формируем строку: [sequence] [line_number] [variable_name] [variable_value]
                    string formattedValue = FormatVariableValue(trackedVariable.VarType, variable.VariableValue);
                    userDataContent.AppendLine($"{variable.Sequence} {trackedVariable.LineNumber} {variable.VariableName} {formattedValue}");
                }

                // Получение ожидаемых шагов программы (для валидации переменных)
                var stepsResponse = await _httpClient.GetAsync($"{_codeControllerUrl}/steps/{algoId}");
                if (!stepsResponse.IsSuccessStatusCode)
                {
                    await LogError($"Failed to get steps: algoId={algoId}, status={stepsResponse.StatusCode}");
                    return StatusCode((int)stepsResponse.StatusCode, "Error fetching steps.");
                }

                var programSteps = await stepsResponse.Content.ReadFromJsonAsync<List<int>>();

                // Создаём временный файл
                var tempFilePath = Path.GetTempFileName();
                await System.IO.File.WriteAllTextAsync(tempFilePath, userDataContent.ToString());

                try
                {
                    // Отправляем запрос к SubstituteValues
                    using var formContent = new MultipartFormDataContent();
                    using var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read);
                    var fileContent = new StreamContent(fileStream);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                    formContent.Add(fileContent, "userDataFile", "user_data.txt");

                    var response = await _httpClient.PostAsync($"{_substituteValuesUrl}/{algoId}/{testData.TestId}", formContent);
                    if (!response.IsSuccessStatusCode)
                    {
                        await LogError($"SubstituteValues failed: testId={testData.TestId}, status={response.StatusCode}");
                        return StatusCode((int)response.StatusCode, "Error calling SubstituteValues.");
                    }

                    var responseContent = await response.Content.ReadAsStringAsync();
                    // Выводим ответ в консоль и лог
                    Console.WriteLine($"SubstituteValues response for testId={testData.TestId}: {responseContent}");
                    await LogSuccess($"SubstituteValues response for testId={testData.TestId}: {responseContent}");

                    var substituteResponse = JsonSerializer.Deserialize<SubstituteValuesResponse>(responseContent);

                    // Сохраняем шаги из Values
                    var valueSteps = substituteResponse?.Values?.Select(v => v.Step).Distinct().ToList() ?? new List<int>();
                    programStepsByTestId[testData.TestId] = valueSteps;
                    await LogSuccess($"Program steps from Values for testId={testData.TestId}: {string.Join(",", valueSteps)}");

                    // Проверка зацикливания программы
                    if (substituteResponse?.CodeModel != null && !substituteResponse.CodeModel.IsSuccessful)
                    {
                        mismatches.Add(new MismatchResponseDto
                        {
                            TestId = testData.TestId,
                            Sequence = 0,
                            VariableName = "",
                            Description = "программа зациклилась"
                        });
                        totalMismatches++;
                        await LogError($"Program looped: testId={testData.TestId}");
                        continue;
                    }

                    // Проверка недостатка шагов пользователя (используем Sequence вместо Step)
                    var missingSequences = valueSteps.Except(userSequences).ToList();
                    if (missingSequences.Any())
                    {
                        foreach (var sequence in missingSequences)
                        {
                            var variables = substituteResponse.Values
                                .Where(v => v.Step == sequence)
                                .Select(v => v.VariableName)
                                .Distinct()
                                .ToList();
                            foreach (var variableName in variables)
                            {
                                mismatches.Add(new MismatchResponseDto
                                {
                                    TestId = testData.TestId,
                                    Sequence = 0,
                                    VariableName = variableName,
                                    Description = "недостаточно шагов пользователя"
                                });
                            }
                        }
                        totalMismatches += missingSequences.Count * substituteResponse.Values
                            .Where(v => missingSequences.Contains(v.Step))
                            .Select(v => v.VariableName)
                            .Distinct()
                            .Count();
                    }

                    // Проверка лишних шагов пользователя (используем Sequence)
                    var extraSequences = userSequences.Except(valueSteps).ToList();
                    if (extraSequences.Any())
                    {
                        foreach (var sequence in extraSequences)
                        {
                            var variables = testData.Variables.Where(v => v.Sequence == sequence).ToList();
                            foreach (var variable in variables)
                            {
                                mismatches.Add(new MismatchResponseDto
                                {
                                    TestId = testData.TestId,
                                    Sequence = variable.Sequence,
                                    VariableName = variable.VariableName,
                                    Description = "лишний шаг"
                                });
                            }
                        }
                        totalMismatches += extraSequences.Count * testData.Variables
                            .Where(v => extraSequences.Contains(v.Sequence))
                            .Select(v => v.VariableName)
                            .Distinct()
                            .Count();
                    }

                    // Обработка ответа
                    if (substituteResponse?.Mismatches != null && substituteResponse.Mismatches.Any())
                    {
                        foreach (var mismatch in substituteResponse.Mismatches)
                        {
                            string description;
                            if (mismatch.ActualValue == "пользователь ушёл в другой шаг")
                            {
                                description = "пользователь ушёл в другой шаг";
                            }
                            else if (mismatch.ExpectedValue != mismatch.ActualValue)
                            {
                                description = "значение пользователя неверно";
                            }
                            else
                            {
                                description = "лишний шаг";
                            }

                            mismatches.Add(new MismatchResponseDto
                            {
                                TestId = testData.TestId,
                                Sequence = testData.Variables.FirstOrDefault(v => v.VariableName == mismatch.VariableName && v.Step == mismatch.Step)?.Sequence ?? 0,
                                VariableName = mismatch.VariableName,
                                Description = description
                            });
                        }
                        totalMismatches += substituteResponse.Mismatches.Count;
                    }
                }
                finally
                {
                    // Удаляем временный файл
                    if (System.IO.File.Exists(tempFilePath))
                    {
                        System.IO.File.Delete(tempFilePath);
                    }
                }
            }

            // Формируем ответ
            if (request.Type == 0)
            {
                // Проверка лишних шагов
                var allExtraSequences = request.Tests
                    .SelectMany(t => t.Variables.Select(v => v.Sequence).Distinct()
                        .Except(programStepsByTestId.GetValueOrDefault(t.TestId) ?? new List<int>()))
                    .Distinct()
                    .ToList();
                if (allExtraSequences.Any())
                {
                    await LogSuccess($"Extra sequences found: {string.Join(",", allExtraSequences)}");
                    return Ok(new IncompleteSolutionResponseDto
                    {
                        Message = "лишний шаг",
                        ExtraSteps = allExtraSequences
                    });
                }

                // Проверка недостатка шагов
                var allMissingSequences = request.Tests
                    .SelectMany(t => (programStepsByTestId.GetValueOrDefault(t.TestId) ?? new List<int>())
                        .Except(t.Variables.Select(v => v.Sequence).Distinct()))
                    .Distinct()
                    .ToList();
                if (allMissingSequences.Any())
                {
                    await LogSuccess($"Missing sequences found: {string.Join(",", allMissingSequences)}");
                    return Ok(new IncompleteSolutionResponseDto
                    {
                        Message = mismatches.Any() ? "ошибки и незавершённое решение" : "пользователь не дозавершил решение",
                        Mismatches = mismatches.Any() ? mismatches : null,
                        MissingSteps = allMissingSequences
                    });
                }

                // Если нет лишних или пропущенных шагов
                if (mismatches.Any())
                {
                    await LogSuccess($"Mismatches found: count={mismatches.Count}");
                    return Ok(mismatches);
                }

                await LogSuccess("No mismatches or sequence issues found");
                return Ok("ошибок не обнаружено");
            }
            else // type == 1
            {
                await LogSuccess($"Type 1: total mismatches={totalMismatches}");
                return Ok(totalMismatches);
            }
        }
        catch (Exception ex)
        {
            await LogError($"Exception: {ex.Message}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }


    private string FormatVariableValue(string varType, string value)
    {
        if (varType.EndsWith("[]"))
        {
            return value; // Формат: "1,2,3"
        }
        else if (varType.EndsWith("[,]"))
        {
            return value; // Формат: "1,2;3,4"
        }
        return value; // Скалярное значение
    }

    private async Task LogError(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.Now}][Upload] Error: {message}\n");
    }

    private async Task LogSuccess(string message)
    {
        await System.IO.File.AppendAllTextAsync(_debugLogPath, $"[{DateTime.Now}][Upload] Success: {message}\n");
    }
}

// DTO для API-запросов
public record TestDto(int TestId, int AlgoId);
public record TrackedVariableDto(int Sequence, int LineNumber, string VarName, string VarType, int Step);
public record IncompleteSolutionResponseDto
{
    public string Message { get; init; }
    public List<MismatchResponseDto>? Mismatches { get; init; }
    public List<int>? ExtraSteps { get; init; }
    public List<int>? MissingSteps { get; init; }
}