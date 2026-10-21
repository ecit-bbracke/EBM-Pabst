using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DocumentRagSystem.Core.Interfaces;
using DocumentRagSystem.Core.Models;
using DocumentRagSystem.Core.Services;
using Moq;
using Xunit;

namespace DocumentRagSystem.UnitTests;

public class ConversationalMultiTurnOrchestratorTests
{
    [Fact]
    public async Task Orchestrator_MultiTurnCompatibilityFollowUp_ShouldRouteToCompatibilityWorkflow()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();

        // 1. Query Refiner response for follow up
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Conversational Query Refiner")), true))
            .ReturnsAsync("""
            {
              "original_question": "Can the remaining one work with Sensor XYZ?",
              "relationship_to_previous_turn": "REFINES",
              "resolved_question": "Can Controller C work with Sensor XYZ?",
              "effective_question": "Is Controller C compatible with Sensor XYZ while satisfying Modbus TCP, 24 VDC, and IP67 requirements?",
              "active_entities": [
                { "type": "controller", "name": "Controller C" },
                { "type": "sensor", "name": "Sensor XYZ" }
              ],
              "active_constraints": [
                { "attribute": "protocol", "operator": "supports", "value": "Modbus TCP", "unit": null, "source_turn": 1, "status": "USER_CONSTRAINT" },
                { "attribute": "supply_voltage", "operator": "supports", "value": "24", "unit": "VDC", "source_turn": 2, "status": "USER_CONSTRAINT" },
                { "attribute": "ingress_protection", "operator": "equals", "value": "IP67", "unit": null, "source_turn": 3, "status": "USER_CONSTRAINT" }
              ],
              "candidate_set": ["Controller C"],
              "references_resolved": ["the remaining one -> Controller C"],
              "clarification_required": false
            }
            """);

        // 2. Input Governor response for effective question
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""
            {
              "intent": "COMPATIBILITY",
              "confidence": 0.96,
              "entities": [
                { "type": "controller", "name": "Controller C" },
                { "type": "sensor", "name": "Sensor XYZ" }
              ],
              "requested_attributes": ["compatibility", "supply_voltage", "protocol"],
              "constraints": ["Modbus TCP", "24 VDC", "IP67"],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        // 3. Compatibility Workflow Executor completion
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Compatibility RAG workflow")), true))
            .ReturnsAsync("""
            {
              "checks": [
                {
                  "dimension": "supply_voltage",
                  "sourceValue": "24 VDC",
                  "targetRequirement": "24 VDC",
                  "status": "COMPATIBLE",
                  "reason": "Both operate on 24 VDC"
                },
                {
                  "dimension": "protocol",
                  "sourceValue": "Modbus TCP",
                  "targetRequirement": "Modbus TCP",
                  "status": "COMPATIBLE",
                  "reason": "Both support Modbus TCP communication"
                }
              ],
              "status": "COMPATIBLE",
              "reason": "Controller C and Sensor XYZ are fully compatible on 24 VDC and Modbus TCP."
            }
            """);

        // 4. Evidence Evaluator
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Evidence Layer")), true))
            .ReturnsAsync("""
            [
              {
                "claim": "Controller C supports 24 VDC and Modbus TCP",
                "status": "SUPPORTED",
                "reason": "Explicitly documented in datasheet.",
                "sources": ["controller_c_spec.pdf"]
              },
              {
                "claim": "Sensor XYZ is compatible with Modbus TCP",
                "status": "SUPPORTED",
                "reason": "Documented in sensor manual.",
                "sources": ["sensor_xyz_manual.pdf"]
              }
            ]
            """);

        // 5. Answer Composer
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("Yes, Controller C is fully compatible with Sensor XYZ across 24 VDC and Modbus TCP.");

        // 6. Output Governor
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[]
            {
                new DocumentChunk("c1", "doc1", "Controller C manual: 24 VDC, Modbus TCP, IP67", 0),
                new DocumentChunk("c2", "doc2", "Sensor XYZ manual: 24 VDC Modbus TCP interface", 1)
            });

        var stateStore = new InMemoryConversationStateStore();
        var priorState = new ConversationState(
            ConversationId: "conv-100",
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
        await stateStore.SaveStateAsync("conv-100", priorState);

        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            queryRefiner: new ConversationalQueryRefiner(mockLlm.Object),
            stateStore: stateStore
        );

        // Act
        var (answer, chunks, trace) = await orchestrator.ProcessQueryAsync("Can the remaining one work with Sensor XYZ?", mockVectorStore.Object, "conv-100");

        // Assert
        Assert.Equal("Compatibility", trace.Workflow);
        Assert.Contains("compatible", answer, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(trace.QueryRefinement);
        Assert.Equal(TurnRelationship.Refines, trace.QueryRefinement.Relationship);
        Assert.Contains("Is Controller C compatible with Sensor XYZ", trace.QueryRefinement.EffectiveQuestion);

        // Verify updated state
        var updatedState = await stateStore.GetStateAsync("conv-100");
        Assert.NotNull(updatedState);
        Assert.Equal(4, updatedState.TurnCount);
        Assert.Contains(updatedState.ActiveEntities, e => e.Name == "Sensor XYZ");
        Assert.NotNull(updatedState.ValidatedFacts);
        Assert.NotEmpty(updatedState.ValidatedFacts);
    }

    [Fact]
    public async Task Orchestrator_PreviousHallucinationContainment_ShouldNotStoreUnsupportedClaimsAsRetrievedFacts()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Conversational Query Refiner")), true))
            .ReturnsAsync("""
            {
              "original_question": "Does it have Bluetooth 5.0?",
              "relationship_to_previous_turn": "CONTINUES",
              "resolved_question": "Does Controller A have Bluetooth 5.0?",
              "effective_question": "Does Controller A support Bluetooth 5.0 wireless communication?",
              "active_entities": [{ "type": "controller", "name": "Controller A" }],
              "active_constraints": [],
              "clarification_required": false
            }
            """);

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""
            {
              "intent": "SPEC_LOOKUP",
              "confidence": 0.9,
              "entities": [{ "type": "controller", "name": "Controller A" }],
              "requested_attributes": ["bluetooth"],
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        mockLlm.Setup(x => x.GenerateResponseAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DocumentChunk>>()))
            .ReturnsAsync("Controller A might support Bluetooth 5.0 according to some rumors.");

        // Evidence Evaluator marks the claim as MISSING/CONFLICTING
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Evidence Layer")), true))
            .ReturnsAsync("""
            [
              {
                "claim": "Controller A supports Bluetooth 5.0",
                "status": "MISSING",
                "reason": "No mention of Bluetooth in any official documentation chunk.",
                "sources": []
              }
            ]
            """);

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("missing or conflicting")), false))
            .ReturnsAsync("Controller A Bluetooth specification");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("There is no documented evidence that Controller A supports Bluetooth 5.0.");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "doc1", "Controller A general manual.", 0) });

        var stateStore = new InMemoryConversationStateStore();
        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            queryRefiner: new ConversationalQueryRefiner(mockLlm.Object),
            stateStore: stateStore
        );

        // Act
        var (_, _, trace) = await orchestrator.ProcessQueryAsync("Does it have Bluetooth 5.0?", mockVectorStore.Object, "conv-hallucination-test");

        // Assert
        var finalState = trace.ConversationStateAfter;
        Assert.NotNull(finalState);
        Assert.NotNull(finalState.ValidatedFacts);
        // Validated facts must NOT contain the MISSING / hallucinated claim
        Assert.DoesNotContain(finalState.ValidatedFacts, f => f.Attribute.Contains("Bluetooth"));
    }

    [Fact]
    public async Task Orchestrator_DerivedFact_ShouldStoreDerivationNotesAndEvidence()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Conversational Query Refiner")), true))
            .ReturnsAsync("""
            {
              "original_question": "Can it operate at 24 VDC?",
              "relationship_to_previous_turn": "CONTINUES",
              "resolved_question": "Can Controller A operate at 24 VDC?",
              "effective_question": "Can Controller A operate at 24 VDC?",
              "active_entities": [{ "type": "controller", "name": "Controller A" }],
              "active_constraints": [{ "attribute": "supply_voltage", "operator": "supports", "value": "24", "unit": "VDC", "source_turn": 1 }],
              "clarification_required": false
            }
            """);

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""
            {
              "intent": "SPEC_LOOKUP",
              "confidence": 0.95,
              "entities": [{ "type": "controller", "name": "Controller A" }],
              "requested_attributes": ["voltage"],
              "constraints": ["24 VDC"],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        mockLlm.Setup(x => x.GenerateResponseAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DocumentChunk>>()))
            .ReturnsAsync("Controller A supports 24 VDC as it falls within the documented 18-30 VDC range.");

        // Evidence Evaluator returns DERIVED claim
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Evidence Layer")), true))
            .ReturnsAsync("""
            [
              {
                "claim": "24 VDC is supported as it is within the documented 18-30 VDC operating range",
                "status": "DERIVED",
                "reason": "Derived from documented voltage specification 18-30 VDC.",
                "sources": ["controller_a_spec.pdf"]
              }
            ]
            """);

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("Controller A supports 24 VDC because 24 VDC is within the documented 18-30 VDC range.");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "doc1", "Voltage range: 18-30 VDC", 0) });

        var stateStore = new InMemoryConversationStateStore();
        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            queryRefiner: new ConversationalQueryRefiner(mockLlm.Object),
            stateStore: stateStore
        );

        // Act
        var (_, _, trace) = await orchestrator.ProcessQueryAsync("Can it operate at 24 VDC?", mockVectorStore.Object, "conv-derived-test");

        // Assert
        var finalState = trace.ConversationStateAfter;
        Assert.NotNull(finalState);
        Assert.NotNull(finalState.DerivedFacts);
        Assert.NotEmpty(finalState.DerivedFacts);
        var derivedFact = finalState.DerivedFacts.First();
        Assert.Equal(ConversationFactStatus.DerivedFact, derivedFact.Status);
        Assert.Contains("18-30 VDC", derivedFact.DerivationNotes);
        Assert.Equal("controller_a_spec.pdf", derivedFact.SourceId);
    }

    [Fact]
    public async Task Orchestrator_AmbiguousPronoun_ShouldTriggerClarificationEarly()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Conversational Query Refiner")), true))
            .ReturnsAsync("""
            {
              "original_question": "Will that run outdoors?",
              "relationship_to_previous_turn": "CONTINUES",
              "resolved_question": "Will that device run outdoors?",
              "effective_question": "Will that device run outdoors?",
              "active_entities": [
                { "type": "controller", "name": "Controller A" },
                { "type": "controller", "name": "Controller B" }
              ],
              "active_constraints": [],
              "clarification_required": true,
              "clarification_reason": "The pronoun 'that' is ambiguous between Controller A and Controller B."
            }
            """);

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("incomplete or unclear")), false))
            .ReturnsAsync("Venligst præciser om du mener Controller A eller Controller B.");

        var mockVectorStore = new Mock<IVectorStore>();
        var stateStore = new InMemoryConversationStateStore();
        var priorState = new ConversationState(
            ConversationId: "conv-ambiguous-test",
            Topic: "multidevice discussion",
            ActiveEntities: new List<ConversationEntity>
            {
                new("controller", "Controller A"),
                new("controller", "Controller B")
            },
            ActiveConstraints: new List<ConversationConstraint>(),
            TurnCount: 2
        );
        await stateStore.SaveStateAsync("conv-ambiguous-test", priorState);

        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            queryRefiner: new ConversationalQueryRefiner(mockLlm.Object),
            stateStore: stateStore
        );

        // Act
        var (answer, chunks, trace) = await orchestrator.ProcessQueryAsync("Will that run outdoors?", mockVectorStore.Object, "conv-ambiguous-test");

        // Assert
        Assert.Equal("Clarification", trace.Workflow);
        Assert.Contains("Controller A eller Controller B", answer);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Orchestrator_TopicChange_ShouldStartFreshStateAndNotLeakOldConstraints()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Conversational Query Refiner")), true))
            .ReturnsAsync("""
            {
              "original_question": "How do I configure gateway Z?",
              "relationship_to_previous_turn": "NEW_TOPIC",
              "resolved_question": "How do I configure gateway Z?",
              "effective_question": "How do I configure gateway Z?",
              "active_entities": [{ "type": "gateway", "name": "Gateway Z" }],
              "active_constraints": [],
              "new_topic": true,
              "clarification_required": false
            }
            """);

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""
            {
              "intent": "PROCEDURE",
              "confidence": 0.95,
              "entities": [{ "type": "gateway", "name": "Gateway Z" }],
              "requested_attributes": ["configuration"],
              "constraints": [],
              "clarification_required": false,
              "clarification_reason": null
            }
            """);

        mockLlm.Setup(x => x.GenerateResponseAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DocumentChunk>>()))
            .ReturnsAsync("Steps to configure Gateway Z: 1. Connect via Ethernet. 2. Open web portal.");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Evidence Layer")), true))
            .ReturnsAsync("""
            [
              {
                "claim": "Configure Gateway Z via Ethernet web portal",
                "status": "SUPPORTED",
                "reason": "Explicitly in gateway guide.",
                "sources": ["gateway_guide.pdf"]
              }
            ]
            """);

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("To configure Gateway Z: connect via Ethernet and access the web portal.");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""
            {
              "approved": true,
              "issues": [],
              "action": "APPROVE"
            }
            """);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new[] { new DocumentChunk("c1", "doc1", "Gateway Z quickstart guide", 0) });

        var stateStore = new InMemoryConversationStateStore();
        var priorState = new ConversationState(
            ConversationId: "conv-leak-test",
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
        await stateStore.SaveStateAsync("conv-leak-test", priorState);

        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            queryRefiner: new ConversationalQueryRefiner(mockLlm.Object),
            stateStore: stateStore
        );

        // Act
        var (_, _, trace) = await orchestrator.ProcessQueryAsync("How do I configure gateway Z?", mockVectorStore.Object, "conv-leak-test");

        // Assert
        var newState = trace.ConversationStateAfter;
        Assert.NotNull(newState);
        Assert.Empty(newState.ActiveConstraints); // Old constraints (Modbus TCP, 24V, IP67) must NOT leak
        Assert.DoesNotContain(newState.ActiveEntities, e => e.Name == "Controller A");
        Assert.Contains(newState.ActiveEntities, e => e.Name == "Gateway Z");
    }

    [Fact]
    public async Task ConversationStateStore_ShouldSaveGetAndClearState()
    {
        // Arrange
        var store = new InMemoryConversationStateStore();
        var state = new ConversationState(
            ConversationId: "conv-123",
            Topic: "fan selection",
            ActiveEntities: new List<ConversationEntity> { new("fan", "A6E450AP0201") },
            ActiveConstraints: new List<ConversationConstraint> { new("diameter", "<=", "450", "mm", 1) },
            TurnCount: 1
        );

        // Act & Assert
        await store.SaveStateAsync("conv-123", state);
        var loaded = await store.GetStateAsync("conv-123");
        Assert.NotNull(loaded);
        Assert.Equal("conv-123", loaded.ConversationId);
        Assert.Equal("A6E450AP0201", loaded.ActiveEntities[0].Name);

        await store.ClearStateAsync("conv-123");
        var cleared = await store.GetStateAsync("conv-123");
        Assert.Null(cleared);
    }

    [Fact]
    public async Task Orchestrator_ShouldPreserveOwnerUserId_AcrossCompletedTurns()
    {
        // Arrange
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync("""{"intent": "SPEC_LOOKUP", "confidence": 0.95, "entities": [{"type": "fan", "name": "K3G560"}], "requested_attributes": [], "constraints": [], "clarification_required": false, "clarification_reason": null}""");

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk> { new("chunk-1", "doc-1", "K3G560 voltage 400V", 0) });

        var stateStore = new InMemoryConversationStateStore();
        var initialState = ConversationState.CreateNew("conv-owner-test", ownerUserId: "engineer@ebmpabst.dk");
        await stateStore.SaveStateAsync("conv-owner-test", initialState);

        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            queryRefiner: new ConversationalQueryRefiner(mockLlm.Object),
            stateStore: stateStore
        );

        // Act
        var (_, _, trace) = await orchestrator.ProcessQueryAsync("What is the voltage of K3G560?", mockVectorStore.Object, "conv-owner-test", initialState);

        // Assert
        Assert.NotNull(trace.ConversationStateAfter);
        Assert.Equal("engineer@ebmpabst.dk", trace.ConversationStateAfter.OwnerUserId);

        var persistedState = await stateStore.GetStateAsync("conv-owner-test");
        Assert.NotNull(persistedState);
        Assert.Equal("engineer@ebmpabst.dk", persistedState.OwnerUserId);
    }

    [Fact]
    public async Task Orchestrator_WhenOutputGovernorRejectsAfterRegenerationRetries_ReturnsFailSafe_AndDoesNotCache()
    {
        // Arrange
        TechnicalRagOrchestrator.InvalidateQueryCache();
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""{"intent": "SPEC_LOOKUP", "confidence": 0.95, "entities": [{"type": "fan", "name": "K3G560"}], "requested_attributes": [], "constraints": [], "clarification_required": false, "clarification_reason": null}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""{"approved": false, "issues": [{"type": "UNSUPPORTED_CLAIM", "claim": "Max current is 50A", "severity": "HIGH"}], "action": "REJECT"}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("Unverified answer: Max current is 50A.");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Regeneration")), false))
            .ReturnsAsync("Rewritten answer: Max current is still 50A.");

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk> { new("chunk-1", "doc-1", "K3G560 voltage 400V", 0) });

        var stateStore = new InMemoryConversationStateStore();
        var convId = $"test-gov-retry-{Guid.NewGuid():N}";
        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            stateStore: stateStore
        );

        // Act
        var (finalAnswer, _, trace) = await orchestrator.ProcessQueryAsync("What is the current of K3G560?", mockVectorStore.Object, convId);

        // Assert
        Assert.NotNull(trace.OutputGovernor);
        Assert.False(trace.OutputGovernor.Approved);
        Assert.Contains("Der kunne ikke formuleres et verificeret svar", finalAnswer);
        Assert.DoesNotContain("50A", finalAnswer);

        // Must not be cached: A second query must execute the pipeline again rather than returning from cache
        await orchestrator.ProcessQueryAsync("What is the current of K3G560?", mockVectorStore.Object, convId);
        mockLlm.Verify(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true), Times.Exactly(2));
    }

    [Fact]
    public async Task Orchestrator_OnCacheHit_DoesNotLeakPromptTracesOrPreviousQueryRefinement()
    {
        // Arrange
        TechnicalRagOrchestrator.InvalidateQueryCache();
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""{"intent": "SPEC_LOOKUP", "confidence": 0.95, "entities": [{"type": "fan", "name": "K3G560"}], "requested_attributes": [], "constraints": [], "clarification_required": false, "clarification_reason": null}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""{"approved": true, "issues": [], "action": "APPROVE"}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("Verified answer: Operating voltage is 400V.");

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk> { new("chunk-1", "doc-1", "K3G560 voltage 400V", 0) });

        var stateStore = new InMemoryConversationStateStore();
        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            stateStore: stateStore
        );

        // User A runs first query in Session A
        var (ans1, _, trace1) = await orchestrator.ProcessQueryAsync("Voltage of K3G560?", mockVectorStore.Object, "session-A");
        Assert.NotNull(trace1);

        // User B runs the same query in Session B (hits cache!)
        var (ans2, _, trace2) = await orchestrator.ProcessQueryAsync("Voltage of K3G560?", mockVectorStore.Object, "session-B");

        // Assert: Diagnostic prompts from Session A are NOT leaked into Session B's trace
        Assert.NotNull(trace2.PromptTraces);
        Assert.Empty(trace2.PromptTraces);
        Assert.Equal("Voltage of K3G560?", trace2.Question);
    }

    [Fact]
    public void IsUserAdmin_WhenUserHasAdminEmail_WithoutAdminRole_ReturnsFalse()
    {
        var identity = new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin@ebmpabst.dk")
        }, "TestAuth");

        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        Assert.False(DocumentRagSystem.WebApi.UserSecurityHelper.IsUserAdmin(principal));
    }

    [Fact]
    public async Task Orchestrator_WhenQueryHasNewlyRefinedConstraints_BypassesCache()
    {
        // Arrange
        TechnicalRagOrchestrator.InvalidateQueryCache();
        var mockLlm = new Mock<ILlmService>();
        // Query 1 has no constraints -> returns generic 230V answer and gets cached
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor") && !s.Contains("24V")), true))
            .ReturnsAsync("""{"intent": "SPEC_LOOKUP", "confidence": 0.95, "entities": [{"type": "fan", "name": "K3G560"}], "requested_attributes": [], "constraints": [], "clarification_required": false, "clarification_reason": null}""");

        // Query 2 adds 24V constraint in refinement
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor") && s.Contains("24V")), true))
            .ReturnsAsync("""{"intent": "SPEC_LOOKUP", "confidence": 0.95, "entities": [{"type": "fan", "name": "K3G560"}], "requested_attributes": [], "constraints": [{"attribute": "supply_voltage", "operator": "eq", "value": "24V"}], "clarification_required": false, "clarification_reason": null}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""{"approved": true, "issues": [], "action": "APPROVE"}""");

        mockLlm.SetupSequence(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("Generic answer: 230V model.")
            .ReturnsAsync("Specific answer: 24V DC model.");

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk> { new("chunk-1", "doc-1", "K3G560 230V AC and 24V DC options", 0) });

        var stateStore = new InMemoryConversationStateStore();
        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            stateStore: stateStore
        );

        // Turn 1: Generic query without constraints -> caches 230V answer
        var (ans1, _, _) = await orchestrator.ProcessQueryAsync("Voltage for K3G560?", mockVectorStore.Object, "session-constraints");
        Assert.Contains("230V", ans1);

        // Turn 2: Query with newly refined 24V constraint -> MUST NOT return the cached 230V answer!
        var (ans2, _, _) = await orchestrator.ProcessQueryAsync("Voltage for K3G560 with 24V?", mockVectorStore.Object, "session-constraints");
        Assert.Contains("24V", ans2);
        Assert.DoesNotContain("230V model", ans2);
    }

    [Fact]
    public async Task Orchestrator_WhenCacheInvalidatedWhileQueryIsInFlight_DoesNotCacheStaleResult()
    {
        // Arrange
        TechnicalRagOrchestrator.InvalidateQueryCache();
        var mockLlm = new Mock<ILlmService>();
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""{"intent": "SPEC_LOOKUP", "confidence": 0.95, "entities": [{"type": "fan", "name": "K3G560"}], "requested_attributes": [], "constraints": [], "clarification_required": false, "clarification_reason": null}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""{"approved": true, "issues": [], "action": "APPROVE"}""");

        // When Answer Composer runs, an upload/deletion occurs and invalidates the cache while the query is in flight!
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .Returns<string, bool>((prompt, isJson) =>
            {
                // In-flight invalidation occurs here!
                TechnicalRagOrchestrator.InvalidateQueryCache();
                return Task.FromResult("Stale answer from old document version.");
            });

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk> { new("chunk-old", "doc-deleted", "Old document text", 0) });

        var stateStore = new InMemoryConversationStateStore();
        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            queryRefiner: new ConversationalQueryRefiner(mockLlm.Object),
            stateStore: stateStore
        );

        // Act: Run query 1, which suffers an in-flight cache invalidation
        var (ans1, _, _) = await orchestrator.ProcessQueryAsync("Voltage for K3G560?", mockVectorStore.Object, "flight-1");
        Assert.Contains("Stale answer", ans1);

        // Now setup mock for Query 2 with a new fresh answer
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("Fresh updated answer from new document version.");

        // Query 2: Must NOT receive the stale answer from cache! It must run again because the in-flight write was rejected.
        var (ans2, _, _) = await orchestrator.ProcessQueryAsync("Voltage for K3G560?", mockVectorStore.Object, "flight-2");
        Assert.Contains("Fresh updated answer", ans2);
        Assert.DoesNotContain("Stale answer", ans2);
    }

    [Fact]
    public async Task Orchestrator_WhenConstraintValueModifiedAcrossTurns_ReplacesOldConstraintAndDoesNotRetainBoth()
    {
        // Arrange
        TechnicalRagOrchestrator.InvalidateQueryCache();
        var mockLlm = new Mock<ILlmService>();

        // Turn 2 Refiner: returns MODIFIES with 48 V in active_constraints
        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Refiner")), true))
            .ReturnsAsync("""{"effective_query": "K3G560 with 48 V", "relationship_to_previous_turn": "MODIFIES", "active_entities": [{"type": "fan", "name": "K3G560"}], "active_constraints": [{"attribute": "supply_voltage", "operator": "eq", "value": "48 V"}], "candidate_set": [], "referenced_facts": []}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Input Governor")), true))
            .ReturnsAsync("""{"intent": "SPEC_LOOKUP", "confidence": 0.95, "entities": [{"type": "fan", "name": "K3G560"}], "requested_attributes": [], "constraints": ["48 V"], "clarification_required": false, "clarification_reason": null}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Output Governor")), true))
            .ReturnsAsync("""{"approved": true, "issues": [], "action": "APPROVE"}""");

        mockLlm.Setup(x => x.GenerateCompletionAsync(It.Is<string>(s => s.Contains("Answer Composer")), false))
            .ReturnsAsync("Verified answer: Operating voltage is documented as 48 V.");

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentChunk> { new("chunk-1", "doc-1", "K3G560 specs", 0) });

        var stateStore = new InMemoryConversationStateStore();
        // Prior state has 24 V constraint
        var priorState = new ConversationState(
            ConversationId: "conv-modify-c",
            Topic: "fan selection",
            ActiveEntities: new List<ConversationEntity> { new("fan", "K3G560") },
            ActiveConstraints: new List<ConversationConstraint> { new("supply_voltage", "eq", "24 V", "V", 1) },
            TurnCount: 1,
            LastEffectiveQuestion: "Voltage for K3G560 with 24 V?"
        );
        await stateStore.SaveStateAsync("conv-modify-c", priorState);

        var orchestrator = new TechnicalRagOrchestrator(
            new InputGovernor(mockLlm.Object),
            new WorkflowRouter(),
            new EvidenceEvaluator(mockLlm.Object),
            new AnswerComposer(mockLlm.Object),
            new OutputGovernor(mockLlm.Object),
            mockLlm.Object,
            queryRefiner: new ConversationalQueryRefiner(mockLlm.Object),
            stateStore: stateStore
        );

        // Act: User modifies constraint to 48 V
        await orchestrator.ProcessQueryAsync("Actually what if we use 48 V?", mockVectorStore.Object, "conv-modify-c");
        var state = await stateStore.GetStateAsync("conv-modify-c");

        // Assert: Must contain ONLY 48 V, and NOT both 24 V and 48 V!
        Assert.NotNull(state);
        Assert.Single(state.ActiveConstraints);
        Assert.Equal("supply_voltage", state.ActiveConstraints[0].Attribute);
        Assert.Equal("48 V", state.ActiveConstraints[0].Value);
    }
}
