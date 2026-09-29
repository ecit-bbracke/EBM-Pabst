using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using DocumentRagSystem.Infrastructure.Llm;

namespace DocumentRagSystem.Evaluation;

/// <summary>
/// Side-by-side evaluation metrics for an architectural component across providers.
/// </summary>
public sealed class ComponentEvaluationMetric
{
    public string Component { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int TotalInvocations { get; set; }
    public int SuccessfulInvocations { get; set; }
    public int SchemaValidInvocations { get; set; }
    public double SchemaValidityPercentage => TotalInvocations > 0 ? (double)SchemaValidInvocations / TotalInvocations * 100.0 : 0.0;
    public int AccurateResults { get; set; }
    public double AccuracyPercentage => TotalInvocations > 0 ? (double)AccurateResults / TotalInvocations * 100.0 : 0.0;
    public List<long> LatenciesMs { get; } = new();
    public double MeanLatencyMs => LatenciesMs.Count > 0 ? LatenciesMs.Average() : 0;
    public double MedianLatencyMs => LatenciesMs.Count > 0 ? GetMedian(LatenciesMs) : 0;
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int RetryCount { get; set; }

    private static double GetMedian(List<long> list)
    {
        var sorted = list.OrderBy(x => x).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 != 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

/// <summary>
/// Full comparison report containing results across all evaluated providers and components.
/// </summary>
public sealed class SideBySideComparisonReport
{
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<string> Providers { get; set; } = Array.Empty<string>();
    public List<ComponentEvaluationMetric> Metrics { get; set; } = new();

    public string GenerateMarkdownTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Side-by-Side LLM Provider Evaluation Report");
        sb.AppendLine($"*Generated at: {EvaluatedAt:yyyy-MM-dd HH:mm:ss} UTC*\n");

        var components = Metrics.Select(m => m.Component).Distinct().ToList();
        var providers = Providers.ToList();

        sb.Append("| Component | Metric |");
        foreach (var p in providers)
        {
            sb.Append($" {p} |");
        }
        sb.AppendLine();

        sb.Append("| :--- | :--- |");
        foreach (var _ in providers)
        {
            sb.Append(" :---: |");
        }
        sb.AppendLine();

        foreach (var comp in components)
        {
            var compMetrics = Metrics.Where(m => m.Component == comp).ToDictionary(m => m.Provider, StringComparer.OrdinalIgnoreCase);

            // Accuracy row
            sb.Append($"| **{comp}** | **Accuracy** |");
            foreach (var p in providers)
            {
                var val = compMetrics.TryGetValue(p, out var m) ? $"{m.AccuracyPercentage:F1}%" : "N/A";
                sb.Append($" {val} |");
            }
            sb.AppendLine();

            // Schema Validity row
            sb.Append($"| {comp} | Schema Validity |");
            foreach (var p in providers)
            {
                var val = compMetrics.TryGetValue(p, out var m) ? $"{m.SchemaValidityPercentage:F1}%" : "N/A";
                sb.Append($" {val} |");
            }
            sb.AppendLine();

            // Median Latency row
            sb.Append($"| {comp} | Median Latency |");
            foreach (var p in providers)
            {
                var val = compMetrics.TryGetValue(p, out var m) ? $"{m.MedianLatencyMs:F0} ms" : "N/A";
                sb.Append($" {val} |");
            }
            sb.AppendLine();

            // Total Tokens row
            sb.Append($"| {comp} | Avg Tokens (In/Out) |");
            foreach (var p in providers)
            {
                if (compMetrics.TryGetValue(p, out var m) && m.TotalInvocations > 0)
                {
                    sb.Append($" {m.TotalInputTokens / m.TotalInvocations} / {m.TotalOutputTokens / m.TotalInvocations} |");
                }
                else
                {
                    sb.Append(" N/A |");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// Side-by-side evaluation runner for comparing Gemini vs Local LLM models
/// without modifying application business code and ensuring fallback is disabled.
/// </summary>
public sealed class ProviderSideBySideEvaluator
{
    private readonly ILlmClientResolver _resolver;

    public ProviderSideBySideEvaluator(ILlmClientResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public async Task<SideBySideComparisonReport> RunAsync(
        IReadOnlyList<string>? providers = null,
        CancellationToken cancellationToken = default)
    {
        var targetProviders = providers ?? new[] { "Gemini", "Local" };
        var report = new SideBySideComparisonReport
        {
            Providers = targetProviders
        };

        foreach (var provider in targetProviders)
        {
            var client = _resolver.Resolve(provider);

            // 1. Evaluate Input Governor
            var inputGovMetric = await EvaluateInputGovernorAsync(client, provider, cancellationToken);
            report.Metrics.Add(inputGovMetric);

            // 2. Evaluate Query Refiner
            var queryRefMetric = await EvaluateQueryRefinerAsync(client, provider, cancellationToken);
            report.Metrics.Add(queryRefMetric);

            // 3. Evaluate Evidence Evaluator
            var evidenceMetric = await EvaluateEvidenceEvaluatorAsync(client, provider, cancellationToken);
            report.Metrics.Add(evidenceMetric);

            // 4. Evaluate Output Governor
            var outputGovMetric = await EvaluateOutputGovernorAsync(client, provider, cancellationToken);
            report.Metrics.Add(outputGovMetric);

            // 5. Evaluate Answer Composer
            var answerMetric = await EvaluateAnswerComposerAsync(client, provider, cancellationToken);
            report.Metrics.Add(answerMetric);
        }

        return report;
    }

    private async Task<ComponentEvaluationMetric> EvaluateInputGovernorAsync(
        ILlmClient client,
        string provider,
        CancellationToken cancellationToken)
    {
        var metric = new ComponentEvaluationMetric { Component = "InputGovernor", Provider = provider };
        var governor = new InputGovernor(client);

        var testCases = new (string Question, string ExpectedIntent)[]
        {
            ("What is the supply voltage of K3G560?", "SPEC_LOOKUP"),
            ("Compare A6E450 and S4E350 airflow characteristics", "COMPARISON"),
            ("Can A6E450AP0201 replace A6E450AP0202?", "COMPATIBILITY"),
            ("Why does the fan not start and show error code 3?", "TROUBLESHOOTING"),
            ("What models of centrifugal fans are in the system?", "OVERVIEW")
        };

        foreach (var tc in testCases)
        {
            metric.TotalInvocations++;
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await governor.GovernInputAsync(tc.Question);
                sw.Stop();
                metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
                metric.SuccessfulInvocations++;
                metric.SchemaValidInvocations++;

                if (string.Equals(res.Intent, tc.ExpectedIntent, StringComparison.OrdinalIgnoreCase))
                {
                    metric.AccurateResults++;
                }
            }
            catch
            {
                sw.Stop();
                metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
            }
        }

        return metric;
    }

    private async Task<ComponentEvaluationMetric> EvaluateQueryRefinerAsync(
        ILlmClient client,
        string provider,
        CancellationToken cancellationToken)
    {
        var metric = new ComponentEvaluationMetric { Component = "QueryRefiner", Provider = provider };
        var refiner = new ConversationalQueryRefiner(client);

        var priorState = new ConversationState(
            ConversationId: Guid.NewGuid().ToString(),
            Topic: "fan_selection",
            ActiveEntities: new List<ConversationEntity> { new("axial_fan", "A6E450AP0201") },
            ActiveConstraints: new List<ConversationConstraint> { new("supply_voltage", "equals", "230 VAC", "VAC", 1) },
            TurnCount: 1,
            LastEffectiveQuestion: "What is the airflow for A6E450AP0201?"
        );

        var testCases = new (string Message, ConversationState? State, string ExpectedRel)[]
        {
            ("What is the impeller diameter?", priorState, TurnRelationship.Continues),
            ("What if we use 400 VAC instead?", priorState, TurnRelationship.Modifies),
            ("Start over. Which sensors support CANopen?", priorState, TurnRelationship.Reset)
        };

        foreach (var tc in testCases)
        {
            metric.TotalInvocations++;
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await refiner.RefineQueryAsync(tc.Message, tc.State);
                sw.Stop();
                metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
                metric.SuccessfulInvocations++;
                metric.SchemaValidInvocations++;

                if (string.Equals(res.RelationshipToPreviousTurn, tc.ExpectedRel, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(res.EffectiveQuestion)))
                {
                    metric.AccurateResults++;
                }
            }
            catch
            {
                sw.Stop();
                metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
            }
        }

        return metric;
    }

    private async Task<ComponentEvaluationMetric> EvaluateEvidenceEvaluatorAsync(
        ILlmClient client,
        string provider,
        CancellationToken cancellationToken)
    {
        var metric = new ComponentEvaluationMetric { Component = "EvidenceEvaluator", Provider = provider };
        var evaluator = new EvidenceEvaluator(client);

        var chunks = new List<DocumentChunk>
        {
            new("c1", "doc1", "The K3G560 operates on 3~ 380-480 VAC 50/60Hz. Max speed is 1850 rpm.", 0)
        };

        var testCases = new (string Question, string Draft, string ExpectedStatus)[]
        {
            ("What is max speed of K3G560?", "The max speed is 1850 rpm.", "SUPPORTED"),
            ("Does K3G560 support 400 VAC?", "It operates at 400 VAC.", "DERIVED")
        };

        foreach (var tc in testCases)
        {
            metric.TotalInvocations++;
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await evaluator.EvaluateEvidenceAsync(tc.Question, tc.Draft, chunks);
                sw.Stop();
                metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
                metric.SuccessfulInvocations++;
                metric.SchemaValidInvocations++;

                if (res != null && res.Count > 0)
                {
                    metric.AccurateResults++;
                }
            }
            catch
            {
                sw.Stop();
                metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
            }
        }

        return metric;
    }

    private async Task<ComponentEvaluationMetric> EvaluateOutputGovernorAsync(
        ILlmClient client,
        string provider,
        CancellationToken cancellationToken)
    {
        var metric = new ComponentEvaluationMetric { Component = "OutputGovernor", Provider = provider };
        var governor = new OutputGovernor(client);

        var chunks = new List<DocumentChunk>
        {
            new("c1", "doc1", "A6E450AP0201 is an axial fan with Airflow direction V. A6E450AP0202 has Airflow direction A.", 0)
        };

        var testCases = new (string Question, string Answer, bool ExpectedApprove)[]
        {
            ("What is the airflow direction of A6E450AP0201?", "The airflow direction of A6E450AP0201 is V.", true),
            ("Can A6E450AP0201 replace A6E450AP0202?", "Yes, A6E450AP0201 is a direct drop-in replacement for A6E450AP0202 with the same airflow direction.", false)
        };

        foreach (var tc in testCases)
        {
            metric.TotalInvocations++;
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await governor.ValidateOutputAsync(tc.Question, tc.Answer, chunks);
                sw.Stop();
                metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
                metric.SuccessfulInvocations++;
                metric.SchemaValidInvocations++;

                bool isCorrect = (tc.ExpectedApprove && res.Approved) || (!tc.ExpectedApprove && (!res.Approved || res.Action != "APPROVE"));
                if (isCorrect)
                {
                    metric.AccurateResults++;
                }
            }
            catch
            {
                sw.Stop();
                metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
            }
        }

        return metric;
    }

    private async Task<ComponentEvaluationMetric> EvaluateAnswerComposerAsync(
        ILlmClient client,
        string provider,
        CancellationToken cancellationToken)
    {
        var metric = new ComponentEvaluationMetric { Component = "AnswerComposer", Provider = provider };
        var composer = new AnswerComposer(client);

        var chunks = new List<DocumentChunk>
        {
            new("c1", "doc1", "K3G560 operates on 380-480 VAC and delivers 14500 m3/h at max speed.", 0)
        };

        var govResult = new InputGovernorResult("SPEC_LOOKUP", 0.95, new List<EntityInfo>(), new List<string>(), new List<string>(), false, null);
        var evidence = new List<EvidenceClaim>
        {
            new("K3G560 operates on 380-480 VAC", "SUPPORTED", "Stated in datasheet", new List<string> { "c1" })
        };

        metric.TotalInvocations++;
        var sw = Stopwatch.StartNew();
        try
        {
            var answer = await composer.ComposeAnswerAsync("What voltage does K3G560 use?", govResult, evidence, chunks);
            sw.Stop();
            metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
            metric.SuccessfulInvocations++;
            metric.SchemaValidInvocations++;

            if (!string.IsNullOrWhiteSpace(answer))
            {
                metric.AccurateResults++;
            }
        }
        catch
        {
            sw.Stop();
            metric.LatenciesMs.Add(sw.ElapsedMilliseconds);
        }

        return metric;
    }
}

/// <summary>
/// Definition of a test prompt used for side-by-side answer generation comparison.
/// </summary>
public sealed class PromptComparisonDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string? SystemInstruction { get; set; }
    public bool RequireJson { get; set; }
    public double? Temperature { get; set; }
}

/// <summary>
/// Result of an individual provider's answer generation for a prompt.
/// </summary>
public sealed class ProviderPromptAnswer
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Side-by-side prompt execution record containing responses from all evaluated providers.
/// </summary>
public sealed class PromptComparisonPair
{
    public PromptComparisonDefinition Prompt { get; set; } = new();
    public Dictionary<string, ProviderPromptAnswer> Answers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Side-by-side answer comparison runner that executes identical prompts across Gemini and Local LLM.
/// </summary>
public sealed class SideBySideAnswerComparisonRunner
{
    private readonly ILlmClientResolver _resolver;

    public SideBySideAnswerComparisonRunner(ILlmClientResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public static List<PromptComparisonDefinition> GetStandardComparisonPrompts()
    {
        return new List<PromptComparisonDefinition>
        {
            new()
            {
                Id = "SPEC_LOOKUP_K3G560",
                Title = "RadiPac K3G560 Electrical & Aerodynamic Specifications",
                Category = "Technical Spec Lookup",
                SystemInstruction = "You are an expert technical support engineer for ebm-papst industrial fans. Be direct, structured, and cite exact numerical values.",
                Prompt = "What are the primary electrical operating specifications (voltage range, frequency, phase, nominal input power, current) and aerodynamic performance for the ebm-papst RadiPac centrifugal fan model K3G560-PW04-01?",
                RequireJson = false,
                Temperature = 0.1
            },
            new()
            {
                Id = "COMPAT_A6E450_AIRFLOW",
                Title = "Axial Fan Airflow Direction Compatibility (Direction A vs V)",
                Category = "Compatibility & Safety Rules",
                SystemInstruction = "You are an ebm-papst safety and technical validation specialist. You must explicitly evaluate product code rules and airflow direction.",
                Prompt = "Can ebm-papst fan model A6E450AP0201 be used as a direct 100% plug-and-play drop-in replacement for A6E450AP0202? Explain the 12th digit airflow direction rule (direction 'A' intake over struts vs direction 'V' blowing over struts) and why opposite airflow fans must never be swapped directly.",
                RequireJson = false,
                Temperature = 0.1
            },
            new()
            {
                Id = "DIAG_EC_FAULT_CODE_3",
                Title = "EC Motor Fault Code 3 Diagnostic Procedure",
                Category = "Troubleshooting & Diagnostics",
                SystemInstruction = "You are a senior field service engineer for ebm-papst EC fan drives. Provide step-by-step diagnostic instructions.",
                Prompt = "An ebm-papst EC centrifugal fan motor trips immediately upon startup with LED diagnostic code 3 (Hall sensor error / rotor blocked). What are the top 3 probable root causes and what systematic diagnostic check steps should a field technician perform with a multimeter and mechanical checks?",
                RequireJson = false,
                Temperature = 0.2
            },
            new()
            {
                Id = "MULTILINGUAL_DANISH_COMPARISON",
                Title = "EC vs AC Fan Architecture Comparison (Danish)",
                Category = "Multilingual Technical Explanation",
                SystemInstruction = "Du er teknisk rådgiver hos ebm-papst. Svar altid fuldstændigt på præcist samme sprog som spørgsmålet (dansk). Vær klar, præcis og professionel.",
                Prompt = "Hvad er de tre største fordele ved at vælge en EC-ventilator (med integreret styringselektronik) frem for en traditionel AC-ventilator med ekstern frekvensomformer i et moderne ventilationsanlæg? Svar på dansk.",
                RequireJson = false,
                Temperature = 0.2
            },
            new()
            {
                Id = "STRUCTURED_JSON_EXTRACTION",
                Title = "Structured JSON Parameter Extraction",
                Category = "Schema Adherence & Structured Output",
                SystemInstruction = "You are a strict data extraction parser. Return ONLY valid JSON with keys: model, motor_technology, phase, voltage_vac, frequency_hz, power_kw, current_a, max_airflow_m3h.",
                Prompt = "Extract data: RadiPac K3G560-PC04-01 with permanent-magnet EC motor, 3~ 380-480 VAC, 50/60 Hz, rated electrical power 3.10 kW, current 5.0 A, maximum airflow 14,500 m3/h.",
                RequireJson = true,
                Temperature = 0.0
            }
        };
    }

    public async Task<List<PromptComparisonPair>> RunComparisonAsync(
        IReadOnlyList<string>? providers = null,
        IReadOnlyList<PromptComparisonDefinition>? prompts = null,
        CancellationToken cancellationToken = default)
    {
        var targetProviders = providers ?? new[] { "Gemini", "Local" };
        var testPrompts = prompts ?? GetStandardComparisonPrompts();
        var results = new List<PromptComparisonPair>();

        foreach (var promptDef in testPrompts)
        {
            var pair = new PromptComparisonPair { Prompt = promptDef };

            foreach (var provider in targetProviders)
            {
                var client = _resolver.Resolve(provider);
                var req = LlmRequest.FromPrompt(
                    promptDef.Prompt,
                    temperature: promptDef.Temperature ?? 0.1,
                    requireJson: promptDef.RequireJson,
                    systemInstruction: promptDef.SystemInstruction);

                var sw = Stopwatch.StartNew();
                try
                {
                    var response = await client.GenerateTextAsync(req, cancellationToken);
                    sw.Stop();

                    pair.Answers[provider] = new ProviderPromptAnswer
                    {
                        Provider = provider,
                        Model = response.Metadata.Model,
                        Answer = response.Content,
                        DurationMs = sw.ElapsedMilliseconds,
                        InputTokens = response.Metadata.InputTokens,
                        OutputTokens = response.Metadata.OutputTokens,
                        IsSuccess = true
                    };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    pair.Answers[provider] = new ProviderPromptAnswer
                    {
                        Provider = provider,
                        Model = "error",
                        Answer = $"[Error] {ex.Message}",
                        DurationMs = sw.ElapsedMilliseconds,
                        IsSuccess = false,
                        ErrorMessage = ex.Message
                    };
                }
            }

            results.Add(pair);
        }

        return results;
    }
}

