using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DocumentRagSystem.E2ETests;

public class DataberegningEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string ValidTestApiKey = "test-databeregning-api-key";

    public DataberegningEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IdentityDb", $"Data Source=identity_databeregning_{Guid.NewGuid():N}.db");
            builder.UseSetting("Authentication:ApiKey", ValidTestApiKey);

            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:ApiKey"] = ValidTestApiKey
                });
            });

            builder.ConfigureServices(services =>
            {
                var storeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IVectorStore));
                if (storeDescriptor != null) services.Remove(storeDescriptor);
                var mockStore = new Mock<IVectorStore>();
                mockStore.Setup(x => x.GetDocumentsAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<Document>());
                services.AddSingleton<IVectorStore>(mockStore.Object);
            });
        });
    }

    [Fact]
    public async Task Databeregningsform_WithoutAuth_RequiresAuthentication()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/databeregningsform");

        (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Redirect).Should().BeTrue();
    }

    [Fact]
    public async Task Databeregningsform_WithApiKey_ReturnsHtmlWithUpdatedElements()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        var response = await client.GetAsync("/databeregningsform");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("EBM-Pabst databeregning");
        content.Should().Contain("Ventilator-ID");
        content.Should().Contain("statiskVirkningsgrad");
        content.Should().Contain("forbrugAarligt");
    }

    [Fact]
    public async Task Databeregning_WithoutWorkqueueConfig_Returns503ProblemDetails()
    {
        var unconfiguredFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Workqueue:User"] = "",
                    ["Workqueue:Password"] = ""
                });
            });
        });

        var client = unconfiguredFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        var response = await client.GetAsync("/databeregning");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Workqueue legitimationsoplysninger er ikke konfigureret");
    }

    [Fact]
    public async Task Databeregning_WhenConfigured_ReturnsValidResponse()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidTestApiKey);

        var response = await client.GetAsync("/databeregning");

        (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.ServiceUnavailable).Should().BeTrue();
    }
}
