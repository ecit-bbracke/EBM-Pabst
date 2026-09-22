using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Infrastructure.Llm;

public class GeminiLlmService : ILlmService
{
    private readonly HttpClient? _httpClient;
    private readonly string? _apiKey;
    private readonly string _model;

    public GeminiLlmService(string? apiKey, string model = "gemini-1.5-flash")
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-1.5-flash" : model;

        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_GEMINI_API_KEY")
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
        }
    }

    public async Task<string> GenerateResponseAsync(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentNullException(nameof(query));

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            return GenerateLocalMockResponse(query, contextChunks);
        }

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

            var prompt = BuildPrompt(query, contextChunks);

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = prompt }
                        }
                    }
                }
            };

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Gemini LLM API error (Status {response.StatusCode}): {errorContent}. Falling back to mock.");
                return GenerateLocalMockResponse(query, contextChunks);
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (jsonResult != null &&
                jsonResult.RootElement.TryGetProperty("candidates", out var candidatesProp) &&
                candidatesProp.ValueKind == JsonValueKind.Array &&
                candidatesProp.GetArrayLength() > 0 &&
                candidatesProp[0].TryGetProperty("content", out var contentProp) &&
                contentProp.TryGetProperty("parts", out var partsProp) &&
                partsProp.ValueKind == JsonValueKind.Array &&
                partsProp.GetArrayLength() > 0 &&
                partsProp[0].TryGetProperty("text", out var textProp))
            {
                return textProp.GetString() ?? string.Empty;
            }

            Console.WriteLine("Failed to parse Gemini LLM response. Falling back to mock.");
            return GenerateLocalMockResponse(query, contextChunks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gemini LLM error: {ex.Message}. Falling back to mock.");
            return GenerateLocalMockResponse(query, contextChunks);
        }
    }

    private string BuildPrompt(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### CONTEXT");
        sb.AppendLine("Brugeren har stillet et spørgsmål i <query> og resultaterne fra en RAG search findes i <answers>");
        sb.AppendLine("### INSTRUCTIONS");
        sb.AppendLine("Besvar brugeren spørgsmål i <query> baseret på informationen i <answers>");
        sb.AppendLine("### CONSTRAINTS");
        sb.AppendLine("- Brug UDELUKKENDE informationen i <answers> til at besvare spørgsmålet i <query>");
        sb.AppendLine("- Ingen præ-ampel");
        sb.AppendLine("### OUTPUT FORMAT");
        sb.AppendLine("- Besvar i høflig og rådgivende tone,  men stadig fyldestgørende.");

        foreach (var chunk in contextChunks)
        {
            // sb.AppendLine($"--- Chunk ID: {chunk.Id} (Document: {chunk.DocumentId}) ---");
            sb.AppendLine("<answers>");
            sb.AppendLine(chunk.Text);
            sb.AppendLine("</answers>");            
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine($"<query>{query}</query>");
        sb.AppendLine("Answer:");

        return sb.ToString();
    }

    private string GenerateLocalMockResponse(string query, IEnumerable<DocumentChunk> contextChunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Local Mock Response] Based on the retrieved context chunks:");
        sb.AppendLine();

        bool hasChunks = false;
        foreach (var chunk in contextChunks)
        {
            hasChunks = true;
            sb.AppendLine($"- From chunk '{chunk.Id}' (Doc: {chunk.DocumentId}): \"{chunk.Text}\"");
        }

        if (!hasChunks)
        {
            sb.AppendLine("- No relevant document context chunks were found to answer this query.");
        }

        sb.AppendLine();
        sb.AppendLine($"This is a mock answer generated locally for the query: '{query}'");
        return sb.ToString();
    }

    public async Task<string> GenerateCompletionAsync(string prompt, bool requireJson = false)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentNullException(nameof(prompt));

        if (_httpClient == null || string.IsNullOrWhiteSpace(_apiKey))
        {
            return GenerateLocalMockCompletion(prompt, requireJson);
        }

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

            object requestBody;
            if (requireJson)
            {
                requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        responseMimeType = "application/json"
                    }
                };
            }
            else
            {
                requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    }
                };
            }

            using var response = await _httpClient.PostAsJsonAsync(url, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Gemini LLM API error (Status {response.StatusCode}): {errorContent}. Falling back to mock.");
                return GenerateLocalMockCompletion(prompt, requireJson);
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (jsonResult != null &&
                jsonResult.RootElement.TryGetProperty("candidates", out var candidatesProp) &&
                candidatesProp.ValueKind == JsonValueKind.Array &&
                candidatesProp.GetArrayLength() > 0 &&
                candidatesProp[0].TryGetProperty("content", out var contentProp) &&
                contentProp.TryGetProperty("parts", out var partsProp) &&
                partsProp.ValueKind == JsonValueKind.Array &&
                partsProp.GetArrayLength() > 0 &&
                partsProp[0].TryGetProperty("text", out var textProp))
            {
                return textProp.GetString() ?? string.Empty;
            }

            Console.WriteLine("Failed to parse Gemini LLM response. Falling back to mock.");
            return GenerateLocalMockCompletion(prompt, requireJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gemini LLM error: {ex.Message}. Falling back to mock.");
            return GenerateLocalMockCompletion(prompt, requireJson);
        }
    }

    private string GenerateLocalMockCompletion(string prompt, bool requireJson)
    {
        if (!requireJson)
        {
            return $"[Local Mock Completion] Response for prompt:\n{prompt.Substring(0, Math.Min(prompt.Length, 100))}...";
        }

        // Return appropriate JSON mock structure based on prompt keyword inspection
        var promptLower = prompt.ToLowerInvariant();

        if (promptLower.Contains("input governor") || promptLower.Contains("intent taxonomy"))
        {
            string intent = "SPEC_LOOKUP";
            var entitiesJson = "[]";
            var requestedAttributesJson = "[]";

            if (promptLower.Contains("compare") || promptLower.Contains("difference"))
            {
                intent = "COMPARISON";
                entitiesJson = "[{\"Type\":\"controller\",\"Name\":\"Model A\"},{\"Type\":\"controller\",\"Name\":\"Model B\"}]";
                requestedAttributesJson = "[\"supply_voltage\",\"communication_protocol\"]";
            }
            else if (promptLower.Contains("compatible") || promptLower.Contains("compatibility") || promptLower.Contains("work with"))
            {
                intent = "COMPATIBILITY";
                entitiesJson = "[{\"Type\":\"controller\",\"Name\":\"ABC-500\"},{\"Type\":\"sensor\",\"Name\":\"XYZ-20\"}]";
                requestedAttributesJson = "[\"supply_voltage\",\"signal_type\"]";
            }
            else if (promptLower.Contains("trouble") || promptLower.Contains("error") || promptLower.Contains("doesn't work") || promptLower.Contains("symptom"))
            {
                intent = "TROUBLESHOOTING";
                entitiesJson = "[{\"Type\":\"device\",\"Name\":\"Controller\"}]";
            }
            else if (promptLower.Contains("calculate") || promptLower.Contains("formula") || promptLower.Contains("voltage") && promptLower.Contains("current"))
            {
                intent = "CALCULATION";
            }
            else if (promptLower.Contains("design"))
            {
                intent = "DESIGN";
            }
            else if (promptLower.Contains("clarify") || promptLower.Contains("missing info"))
            {
                intent = "CLARIFICATION";
            }

            return $$"""
            {
              "intent": "{{intent}}",
              "confidence": 0.95,
              "entities": {{entitiesJson}},
              "requested_attributes": {{requestedAttributesJson}},
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """;
        }

        if (promptLower.Contains("comparison rag") || promptLower.Contains("compare multiple entities"))
        {
            return """
            {
              "entities": {
                "Model A": {
                  "name": "Model A",
                  "attributes": {
                    "supply_voltage": {
                      "value": "18-30 VDC",
                      "source": "Model A datasheet"
                    },
                    "communication_protocol": {
                      "value": "Modbus RTU",
                      "source": "Model A manual"
                    }
                  }
                },
                "Model B": {
                  "name": "Model B",
                  "attributes": {
                    "supply_voltage": {
                      "value": "24 VDC",
                      "source": "Model B specifications"
                    },
                    "communication_protocol": {
                      "value": "CANopen",
                      "source": "Model B manual"
                    }
                  }
                }
              }
            }
            """;
        }

        if (promptLower.Contains("compatibility rag") || promptLower.Contains("compatibility questions"))
        {
            return """
            {
              "checks": [
                {
                  "dimension": "supply_voltage",
                  "sourceValue": "24 VDC +/-10%",
                  "targetRequirement": "18-30 VDC",
                  "status": "COMPATIBLE",
                  "reason": "The source voltage range is within the target operating range."
                },
                {
                  "dimension": "communication_protocol",
                  "sourceValue": "Modbus RTU",
                  "targetRequirement": "CANopen",
                  "status": "INCOMPATIBLE",
                  "reason": "Protocols do not match. Conversion device is required."
                }
              ],
              "status": "INCOMPATIBLE",
              "reason": "The communication protocols are incompatible."
            }
            """;
        }

        if (promptLower.Contains("diagnostic rag") || promptLower.Contains("troubleshooting questions"))
        {
            return """
            {
              "causes": [
                {
                  "cause": "Supply voltage below operating range",
                  "status": "SUPPORTED",
                  "evidence": ["Manual section 4.2"],
                  "check": "Measure voltage at terminals X and Y.",
                  "expectedResult": "18-30 VDC"
                }
              ],
              "troubleshootingSteps": [
                "Verify input power source is active.",
                "Check wiring connection at terminals X and Y."
              ]
            }
            """;
        }

        if (promptLower.Contains("evidence layer") || promptLower.Contains("supported, derived, conflicting, missing"))
        {
            return """
            [
              {
                "claim": "The device operates at 24 VDC.",
                "status": "DERIVED",
                "reason": "Operating voltage is stated as 18-30 VDC in the manual.",
                "sources": ["Manual page 12"]
              },
              {
                "claim": "Maximum current is 1.5A.",
                "status": "SUPPORTED",
                "reason": "The datasheet states max current is 1.5A.",
                "sources": ["Datasheet page 2"]
              }
            ]
            """;
        }

        if (promptLower.Contains("output governor") || promptLower.Contains("validate the proposed answer"))
        {
            return """
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }
            """;
        }

        // Default generic JSON
        return "{}";
    }
}
