using System.Collections.Generic;
using System.Text.Json;
using DocumentRagSystem.WebApi.Endpoints;
using FluentAssertions;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class DataberegningTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Databeregning_CanDeserialize_FromFrontendFlatMapStructure()
    {
        // Arrange - JSON representing the structure produced by the updated frontend flatMap logic
        var json = """
        {
          "installeringssted": "Bygning A - Tag",
          "kunde": "Test Kunde A/S",
          "kundesAdresse": "Industrivej 12, 5000 Odense",
          "kontakt": "Jens Hansen",
          "ventilatorer": [
            {
              "ventilatorId": "VE01 ind",
              "luftmaengdeMaalt": "3500",
              "luftmaengdeMax": "4000",
              "statiskTryk": "450",
              "totalTryk": "",
              "kammermaalH": "800",
              "kammermaalD": "600",
              "kammermaalL": "1200",
              "aarligDriftstid": "4000",
              "statiskVirkningsgrad": "65,2",
              "stroem": "3.5",
              "spaending": "400",
              "cos": "0.85",
              "optagetEffekt": "1.8",
              "forbrugAarligt": "7200"
            },
            {
              "ventilatorId": "VE01 ud",
              "luftmaengdeMaalt": "3200",
              "luftmaengdeMax": "",
              "statiskTryk": "400",
              "totalTryk": "",
              "kammermaalH": "800",
              "kammermaalD": "600",
              "kammermaalL": "1200",
              "aarligDriftstid": "4000",
              "statiskVirkningsgrad": "62,5",
              "stroem": "",
              "spaending": "",
              "cos": "",
              "optagetEffekt": "1.5",
              "forbrugAarligt": "6000"
            }
          ]
        }
        """;

        // Act
        var result = JsonSerializer.Deserialize<Databeregning>(json, JsonOptions);

        // Assert
        result.Should().NotBeNull();
        result!.Installeringssted.Should().Be("Bygning A - Tag");
        result.Kunde.Should().Be("Test Kunde A/S");
        result.KundesAdresse.Should().Be("Industrivej 12, 5000 Odense");
        result.Kontakt.Should().Be("Jens Hansen");
        result.Ventilatorer.Should().HaveCount(2);

        var ind = result.Ventilatorer[0];
        ind.VentilatorId.Should().Be("VE01 ind");
        ind.LuftmaengdeMaalt.Should().Be("3500");
        ind.StatiskTryk.Should().Be("450");
        ind.StatiskVirkningsgrad.Should().Be("65,2");
        ind.ForbrugAarligt.Should().Be("7200");

        var ud = result.Ventilatorer[1];
        ud.VentilatorId.Should().Be("VE01 ud");
        ud.LuftmaengdeMaalt.Should().Be("3200");
        ud.StatiskTryk.Should().Be("400");
        ud.StatiskVirkningsgrad.Should().Be("62,5");
        ud.ForbrugAarligt.Should().Be("6000");
    }

    [Fact]
    public void WorkqueueItem_CanDeserialize_WithEmbeddedDataberegningInput()
    {
        // Arrange
        var databeregning = new Databeregning(
            "Teststed",
            "Kunde ApS",
            "Vej 1",
            "Peter",
            new List<DataberegningInput>
            {
                new(
                    "VE01 ind", "2500", "3000", "350", "",
                    "500", "500", "800", "3000", "58,4",
                    "2.1", "400", "0.8", "1.2", "3600"
                )
            });

        var inputJson = JsonSerializer.Serialize(databeregning);
        var queueItemJson = $$"""
        {
          "DataID": 1001,
          "TITEL": "Teststed - 09. oktober 2026",
          "ROBOT": "287700",
          "CreatedAt": "2026-10-09T10:00:00Z",
          "INPUT": {{JsonSerializer.Serialize(inputJson)}}
        }
        """;

        // Act
        var item = JsonSerializer.Deserialize<WorkqueueItem>(queueItemJson, JsonOptions);

        // Assert
        item.Should().NotBeNull();
        item!.DataID.Should().Be(1001);
        item.TITEL.Should().Be("Teststed - 09. oktober 2026");
        item.INPUT.Should().NotBeNullOrWhiteSpace();

        var parsedInput = JsonSerializer.Deserialize<Databeregning>(item.INPUT);
        parsedInput.Should().NotBeNull();
        parsedInput!.Installeringssted.Should().Be("Teststed");
        parsedInput.Ventilatorer.Should().ContainSingle();
        parsedInput.Ventilatorer[0].VentilatorId.Should().Be("VE01 ind");
        parsedInput.Ventilatorer[0].ForbrugAarligt.Should().Be("3600");
    }
}
