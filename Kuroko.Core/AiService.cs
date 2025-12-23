using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kuroko.Core;

public class AiService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly string _systemPrompt;

    // Updated default to Gemma 27B per request (Better rate limits than Gemini Flash usually)
    private const string DefaultModelFallback = "google/gemma-3-27b-it:free";

    // Public constant so UI can access the default for "Reset" functionality
    public const string DefaultSystemPrompt = """
            You are Kuroko, an invisible interview assistant. 
            Your goal is to help the user answer interview questions naturally.
            
            RULES:
            1. Output strictly 3-5 concise bullet points.
            2. Do not write full scripts or long paragraphs.
            3. Focus on metrics, keywords, and specific technical details.
            4. If provided, use the 'RAG Context' to ground your answers in the user's resume.
            5. Keep the tone professional, confident, and conversational.
            """;

    public AiService(string apiKey, string? modelId = null, string? systemPrompt = null)
    {
        _modelId = !string.IsNullOrWhiteSpace(modelId) ? modelId : DefaultModelFallback;
        _systemPrompt = !string.IsNullOrWhiteSpace(systemPrompt) ? systemPrompt : DefaultSystemPrompt;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(120)
        };

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Kuroko-AI");
        _httpClient.DefaultRequestHeaders.Add("X-Title", "Kuroko");
    }

    public async IAsyncEnumerable<string> GetInterviewAssistanceStreamAsync(string transcript, string contextData = "", [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(contextData))
        {
            sb.AppendLine("--- RELEVANT RESUME CONTEXT ---");
            sb.AppendLine(contextData);
            sb.AppendLine("-------------------------------");
        }

        sb.AppendLine("--- LIVE TRANSCRIPT (INTERVIEWER & USER) ---");
        sb.AppendLine(transcript);
        sb.AppendLine("--------------------------------------------");
        sb.AppendLine("Based on the transcript above, provide bullet points to answer the interviewer's last question or continue the topic.");

        string userPrompt = sb.ToString();

        var requestBody = new
        {
            model = _modelId,
            messages = new[]
            {
                new { role = "system", content = _systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.7,
            max_tokens = 250,
            stream = true
        };

        var jsonContent = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            // Descriptive Error Handling
            if ((int)response.StatusCode == 429)
            {
                yield return "**API LIMIT REACHED (429)**\n\n" +
                             "The current model is rate-limited or busy.\n" +
                             "**Suggestion:** Go to Settings -> Intelligence and try a different model (e.g., 'mistralai/devstral-2512:free').";
            }
            else if ((int)response.StatusCode == 401)
            {
                yield return "**AUTHENTICATION ERROR (401)**\n\n" +
                             "Your API Key is invalid or missing.\n" +
                             "**Suggestion:** Check your key in Settings.";
            }
            else
            {
                yield return $"**AI Error ({response.StatusCode}):**\n{errorContent}";
            }
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        // Fix: Use standard read loop instead of checking .EndOfStream which can block or fail on network streams
        while ((line = await reader.ReadLineAsync()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: "))
            {
                var data = line.Substring(6).Trim();
                if (data == "[DONE]") break;

                string? deltaContent = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta))
                        {
                            if (delta.TryGetProperty("content", out var content))
                            {
                                deltaContent = content.GetString();
                            }
                        }
                    }
                }
                catch { /* Ignore partial frames */ }

                if (!string.IsNullOrEmpty(deltaContent))
                {
                    yield return deltaContent;
                }
            }
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}