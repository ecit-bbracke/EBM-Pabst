using System.Collections.Generic;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class ConversationalQueryRefinerTests
{
    [Fact]
    public async Task SingleTurn_WithoutPriorState_ShouldReturnOriginalQuestionAsEffectiveQuestion()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        var refiner = new ConversationalQueryRefiner(mockLlm.Object);

        // Act
        var result = await refiner.RefineQueryAsync("What is the maximum voltage for Controller A?", null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("What is the maximum voltage for Controller A?", result.EffectiveQuestion);
        Assert.Equal("What is the maximum voltage for Controller A?", result.OriginalQuestion);
        Assert.False(result.ClarificationRequired);
    }

    [Fact]
    public async Task ReferenceResolution_ShouldResolvePronounToCandidateEntities()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "original_question": "Which of those supports PoE?",
              "relationship_to_previous_turn": "REFINES",
              "resolved_question": "Which of Model A and Model B supports PoE?",
              "effective_question": "Which of Model A and Model B supports PoE (Power over Ethernet)?",
              "active_entities": [
                { "type": "controller", "name": "Model A" },
                { "type": "controller", "name": "Model B" }
              ],
              "active_constraints": [
                { "attribute": "protocol", "operator": "supports", "value": "EtherCAT", "unit": null, "source_turn": 1, "status": "USER_CONSTRAINT" },
                { "attribute": "power", "operator": "supports", "value": "PoE", "unit": null, "source_turn": 2, "status": "USER_CONSTRAINT" }
              ],
              "candidate_set": ["Model A", "Model B"],
              "references_resolved": ["those -> Model A, Model B"],
              "clarification_required": false
            }
            """);

        var refiner = new ConversationalQueryRefiner(mockLlm.Object);
        var state = new ConversationState(
            ConversationId: "conv-1",
            Topic: "EtherCAT controllers",
            ActiveEntities: new List<ConversationEntity>
            {
                new("controller", "Model A"),
                new("controller", "Model B")
            },
            ActiveConstraints: new List<ConversationConstraint>
            {
                new("protocol", "supports", "EtherCAT", null, 1)
            },
            CandidateSet: new CandidateSet("controller", new List<string> { "Model A", "Model B" }, 1),
            TurnCount: 1
        );

        // Act
        var result = await refiner.RefineQueryAsync("Which of those supports PoE?", state);

        // Assert
        Assert.Equal(TurnRelationship.Refines, result.RelationshipToPreviousTurn);
        Assert.Contains("Model A and Model B", result.ResolvedQuestion);
        Assert.Equal(2, result.ActiveEntities.Count);
        Assert.Equal(2, result.ActiveConstraints.Count);
        Assert.Contains("those -> Model A, Model B", result.ReferencesResolved);
    }

    [Fact]
    public async Task AdditiveFiltering_ShouldAccumulateConstraints()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "original_question": "Which also have a maximum diameter of 20 mm?",
              "relationship_to_previous_turn": "REFINES",
              "resolved_question": "Which sensors supporting IO-Link and operating at -20 C also have a maximum diameter of 20 mm?",
              "effective_question": "Which sensors support IO-Link, operate at minimum temperature <= -20 C, and have diameter <= 20 mm?",
              "active_entities": [
                { "type": "sensor", "name": "sensors" }
              ],
              "active_constraints": [
                { "attribute": "protocol", "operator": "supports", "value": "IO-Link", "unit": null, "source_turn": 1, "status": "USER_CONSTRAINT" },
                { "attribute": "temperature_min", "operator": "<=", "value": "-20", "unit": "C", "source_turn": 2, "status": "USER_CONSTRAINT" },
                { "attribute": "diameter", "operator": "<=", "value": "20", "unit": "mm", "source_turn": 3, "status": "USER_CONSTRAINT" }
              ],
              "constraints_added": [
                { "attribute": "diameter", "operator": "<=", "value": "20", "unit": "mm", "source_turn": 3, "status": "USER_CONSTRAINT" }
              ],
              "clarification_required": false
            }
            """);

        var refiner = new ConversationalQueryRefiner(mockLlm.Object);
        var state = new ConversationState(
            ConversationId: "conv-sensors",
            Topic: "sensor selection",
            ActiveEntities: new List<ConversationEntity> { new("sensor", "sensors") },
            ActiveConstraints: new List<ConversationConstraint>
            {
                new("protocol", "supports", "IO-Link", null, 1),
                new("temperature_min", "<=", "-20", "C", 2)
            },
            TurnCount: 2
        );

        // Act
        var result = await refiner.RefineQueryAsync("Which also have a maximum diameter of 20 mm?", state);

        // Assert
        Assert.Equal(3, result.ActiveConstraints.Count);
        Assert.Contains(result.ActiveConstraints, c => c.Attribute == "diameter" && c.Value == "20");
        Assert.Contains(result.ActiveConstraints, c => c.Attribute == "protocol" && c.Value == "IO-Link");
        Assert.Contains(result.ActiveConstraints, c => c.Attribute == "temperature_min" && c.Value == "-20");
    }

    [Fact]
    public async Task CandidateSetNarrowing_ShouldNarrowCandidatesAcrossTurns()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "original_question": "Which are IP67?",
              "relationship_to_previous_turn": "REFINES",
              "resolved_question": "Which of Controller A and Controller C are IP67?",
              "effective_question": "Which of Controller A and Controller C have ingress protection IP67, while supporting Modbus TCP and 24 VDC?",
              "active_entities": [
                { "type": "controller", "name": "Controller C" }
              ],
              "active_constraints": [
                { "attribute": "protocol", "operator": "supports", "value": "Modbus TCP", "unit": null, "source_turn": 1, "status": "USER_CONSTRAINT" },
                { "attribute": "supply_voltage", "operator": "supports", "value": "24", "unit": "VDC", "source_turn": 2, "status": "USER_CONSTRAINT" },
                { "attribute": "ingress_protection", "operator": "equals", "value": "IP67", "unit": null, "source_turn": 3, "status": "USER_CONSTRAINT" }
              ],
              "candidate_set": ["Controller C"],
              "clarification_required": false
            }
            """);

        var refiner = new ConversationalQueryRefiner(mockLlm.Object);
        var state = new ConversationState(
            ConversationId: "conv-candidates",
            Topic: "controller selection",
            ActiveEntities: new List<ConversationEntity>
            {
                new("controller", "Controller A"),
                new("controller", "Controller C")
            },
            ActiveConstraints: new List<ConversationConstraint>
            {
                new("protocol", "supports", "Modbus TCP", null, 1),
                new("supply_voltage", "supports", "24", "VDC", 2)
            },
            CandidateSet: new CandidateSet("controller", new List<string> { "Controller A", "Controller C" }, 2),
            TurnCount: 2
        );

        // Act
        var result = await refiner.RefineQueryAsync("Which are IP67?", state);

        // Assert
        Assert.NotNull(result.CandidateSet);
        Assert.Single(result.CandidateSet);
        Assert.Equal("Controller C", result.CandidateSet[0]);
    }

    [Fact]
    public async Task ConstraintReplacement_ShouldReplaceOldConstraintNotCombine()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "original_question": "What if we use 48 V instead?",
              "relationship_to_previous_turn": "MODIFIES",
              "resolved_question": "What if Controller C uses 48 VDC supply voltage instead of 24 VDC?",
              "effective_question": "Can Controller C operate at 48 VDC supply voltage while satisfying Modbus TCP and IP67?",
              "active_entities": [
                { "type": "controller", "name": "Controller C" }
              ],
              "active_constraints": [
                { "attribute": "protocol", "operator": "supports", "value": "Modbus TCP", "unit": null, "source_turn": 1, "status": "USER_CONSTRAINT" },
                { "attribute": "ingress_protection", "operator": "equals", "value": "IP67", "unit": null, "source_turn": 3, "status": "USER_CONSTRAINT" },
                { "attribute": "supply_voltage", "operator": "supports", "value": "48", "unit": "VDC", "source_turn": 4, "status": "USER_CONSTRAINT" }
              ],
              "constraints_replaced": [
                {
                  "old_attribute": "supply_voltage",
                  "old_value": "24",
                  "new_constraint": {
                    "attribute": "supply_voltage",
                    "operator": "supports",
                    "value": "48",
                    "unit": "VDC",
                    "source_turn": 4,
                    "status": "USER_CONSTRAINT"
                  }
                }
              ],
              "candidate_set": ["Controller C"],
              "clarification_required": false
            }
            """);

        var refiner = new ConversationalQueryRefiner(mockLlm.Object);
        var state = new ConversationState(
            ConversationId: "conv-replace",
            Topic: "controller selection",
            ActiveEntities: new List<ConversationEntity> { new("controller", "Controller C") },
            ActiveConstraints: new List<ConversationConstraint>
            {
                new("protocol", "supports", "Modbus TCP", null, 1),
                new("supply_voltage", "supports", "24", "VDC", 2),
                new("ingress_protection", "equals", "IP67", null, 3)
            },
            CandidateSet: new CandidateSet("controller", new List<string> { "Controller C" }, 3),
            TurnCount: 3
        );

        // Act
        var result = await refiner.RefineQueryAsync("What if we use 48 V instead?", state);

        // Assert
        Assert.Equal(TurnRelationship.Modifies, result.RelationshipToPreviousTurn);
        Assert.Single(result.ConstraintsReplaced);
        Assert.Equal("supply_voltage", result.ConstraintsReplaced[0].OldAttribute);
        Assert.Contains(result.ActiveConstraints, c => c.Attribute == "supply_voltage" && c.Value == "48");
        Assert.DoesNotContain(result.ActiveConstraints, c => c.Attribute == "supply_voltage" && c.Value == "24");
    }

    [Fact]
    public async Task ConstraintRemoval_ShouldRemoveTargetConstraint()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "original_question": "Ignore IP67 requirement.",
              "relationship_to_previous_turn": "MODIFIES",
              "resolved_question": "Ignore the IP67 ingress protection requirement for Controller C.",
              "effective_question": "Which controllers support Modbus TCP and 24 VDC operation without IP67 requirement?",
              "active_entities": [
                { "type": "controller", "name": "Controller C" }
              ],
              "active_constraints": [
                { "attribute": "protocol", "operator": "supports", "value": "Modbus TCP", "unit": null, "source_turn": 1, "status": "USER_CONSTRAINT" },
                { "attribute": "supply_voltage", "operator": "supports", "value": "24", "unit": "VDC", "source_turn": 2, "status": "USER_CONSTRAINT" }
              ],
              "constraints_removed": [
                { "attribute": "ingress_protection", "operator": "equals", "value": "IP67", "unit": null, "source_turn": 3, "status": "USER_CONSTRAINT" }
              ],
              "clarification_required": false
            }
            """);

        var refiner = new ConversationalQueryRefiner(mockLlm.Object);
        var state = new ConversationState(
            ConversationId: "conv-remove",
            Topic: "controller selection",
            ActiveEntities: new List<ConversationEntity> { new("controller", "Controller C") },
            ActiveConstraints: new List<ConversationConstraint>
            {
                new("protocol", "supports", "Modbus TCP", null, 1),
                new("supply_voltage", "supports", "24", "VDC", 2),
                new("ingress_protection", "equals", "IP67", null, 3)
            },
            TurnCount: 3
        );

        // Act
        var result = await refiner.RefineQueryAsync("Ignore IP67 requirement.", state);

        // Assert
        Assert.Single(result.ConstraintsRemoved);
        Assert.Equal("ingress_protection", result.ConstraintsRemoved[0].Attribute);
        Assert.DoesNotContain(result.ActiveConstraints, c => c.Attribute == "ingress_protection");
    }

    [Fact]
    public async Task TopicChange_ShouldNotLeakOldConstraintsIntoNewTopic()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "original_question": "How do I configure gateway Z?",
              "relationship_to_previous_turn": "NEW_TOPIC",
              "resolved_question": "How do I configure gateway Z?",
              "effective_question": "How do I configure gateway Z?",
              "active_entities": [
                { "type": "gateway", "name": "Gateway Z" }
              ],
              "active_constraints": [],
              "candidate_set": [],
              "new_topic": true,
              "clarification_required": false
            }
            """);

        var refiner = new ConversationalQueryRefiner(mockLlm.Object);
        var state = new ConversationState(
            ConversationId: "conv-topic-change",
            Topic: "controller selection",
            ActiveEntities: new List<ConversationEntity> { new("controller", "Controller A") },
            ActiveConstraints: new List<ConversationConstraint>
            {
                new("protocol", "supports", "Modbus TCP", null, 1),
                new("supply_voltage", "supports", "24", "VDC", 2),
                new("ingress_protection", "equals", "IP67", null, 3)
            },
            CandidateSet: new CandidateSet("controller", new List<string> { "Controller A" }, 3),
            TurnCount: 3
        );

        // Act
        var result = await refiner.RefineQueryAsync("How do I configure gateway Z?", state);

        // Assert
        Assert.Equal(TurnRelationship.NewTopic, result.RelationshipToPreviousTurn);
        Assert.True(result.NewTopic);
        Assert.Equal("How do I configure gateway Z?", result.EffectiveQuestion);
        Assert.Empty(result.ActiveConstraints);
        Assert.Single(result.ActiveEntities);
        Assert.Equal("Gateway Z", result.ActiveEntities[0].Name);
    }

    [Fact]
    public async Task AmbiguousPronoun_ShouldTriggerClarificationRequired()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), true))
            .ReturnsAsync("""
            {
              "original_question": "Will that work outdoors?",
              "relationship_to_previous_turn": "CONTINUES",
              "resolved_question": "Will that device work outdoors?",
              "effective_question": "Will that work outdoors?",
              "active_entities": [
                { "type": "controller", "name": "Controller A" },
                { "type": "controller", "name": "Controller B" },
                { "type": "sensor", "name": "Sensor X" }
              ],
              "active_constraints": [],
              "clarification_required": true,
              "clarification_reason": "The reference 'that' could refer to Controller A, Controller B, or Sensor X."
            }
            """);

        var refiner = new ConversationalQueryRefiner(mockLlm.Object);
        var state = new ConversationState(
            ConversationId: "conv-ambiguous",
            Topic: "multidevice discussion",
            ActiveEntities: new List<ConversationEntity>
            {
                new("controller", "Controller A"),
                new("controller", "Controller B"),
                new("sensor", "Sensor X")
            },
            ActiveConstraints: new List<ConversationConstraint>(),
            TurnCount: 2
        );

        // Act
        var result = await refiner.RefineQueryAsync("Will that work outdoors?", state);

        // Assert
        Assert.True(result.ClarificationRequired);
        Assert.NotNull(result.ClarificationReason);
        Assert.Contains("Controller A, Controller B, or Sensor X", result.ClarificationReason);
    }
}
