using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DocumentRagSystem.Core.Interfaces;

/// <summary>
/// Service responsible for rendering email HTML templates with placeholder replacements.
/// </summary>
public interface IEmailTemplateService
{
    /// <summary>
    /// Renders an email template by name with the given dictionary of key-value replacements.
    /// Placeholders in the template are formatted as {{Key}}.
    /// </summary>
    /// <param name="templateName">The name of the template without extension (e.g. "welcome-user").</param>
    /// <param name="parameters">Key-value dictionary of values to substitute into the template.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>Rendered HTML email body.</returns>
    Task<string> RenderTemplateAsync(
        string templateName,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken ct = default);
}
