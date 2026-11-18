using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DocumentRagSystem.Infrastructure.Notifications;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class EmailTemplateServiceTests
{
    [Fact]
    public async Task renderTemplateAsync_withExistingTemplateFile_shouldSubstitutePlaceholders()
    {
        // GIVEN a temporary directory containing an HTML template with placeholders:
        var tempDir = Path.Combine(Path.GetTempPath(), "email_templates_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var templatePath = Path.Combine(tempDir, "custom-welcome.html");
            await File.WriteAllTextAsync(templatePath, "<h1>Velkommen {{Email}}</h1><p>Kode: {{Password}}</p><a href=\"{{LoginUrl}}\">Login</a>");

            var service = new EmailTemplateService(tempDir);
            var parameters = new Dictionary<string, string?>
            {
                ["Email"] = "test@ebmpabst.dk",
                ["Password"] = "Secret123!",
                ["LoginUrl"] = "https://portal.ebmpabst.dk/login.html"
            };

            // WHEN rendering the template:
            var rendered = await service.RenderTemplateAsync("custom-welcome", parameters);

            // THEN placeholders are replaced with encoded values:
            Assert.Contains("Velkommen test@ebmpabst.dk", rendered);
            Assert.Contains("Kode: Secret123!", rendered);
            Assert.Contains("https://portal.ebmpabst.dk/login.html", rendered);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task renderTemplateAsync_withSpecialCharacters_shouldHtmlEncodeValues()
    {
        // GIVEN a template service and parameters containing HTML characters:
        var tempDir = Path.Combine(Path.GetTempPath(), "email_templates_encode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var templatePath = Path.Combine(tempDir, "encode-test.html");
            await File.WriteAllTextAsync(templatePath, "<div>{{Comment}}</div>");

            var service = new EmailTemplateService(tempDir);
            var parameters = new Dictionary<string, string?>
            {
                ["Comment"] = "<script>alert('xss');</script> & \"quotes\""
            };

            // WHEN rendering the template:
            var rendered = await service.RenderTemplateAsync("encode-test", parameters);

            // THEN special characters are safely escaped:
            Assert.DoesNotContain("<script>", rendered);
            Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;);&lt;/script&gt; &amp; &quot;quotes&quot;", rendered);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task renderTemplateAsync_withMissingTemplateFile_shouldFallbackToBuiltInTemplateWithoutThrowing()
    {
        // GIVEN a template service pointing to an empty directory:
        var emptyDir = Path.Combine(Path.GetTempPath(), "empty_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);

        try
        {
            var service = new EmailTemplateService(emptyDir);
            var parameters = new Dictionary<string, string?>
            {
                ["Email"] = "user@test.dk",
                ["Password"] = "TempPass123",
                ["LoginUrl"] = "http://localhost/login"
            };

            // WHEN rendering a non-existent welcome template:
            var rendered = await service.RenderTemplateAsync("welcome-user", parameters);

            // THEN the fallback template is used and populated:
            Assert.NotNull(rendered);
            Assert.Contains("user@test.dk", rendered);
            Assert.Contains("TempPass123", rendered);
        }
        finally
        {
            try { Directory.Delete(emptyDir, true); } catch { }
        }
    }
}
