using System.Threading.Tasks;
using DocumentRagSystem.Core.Models;

namespace DocumentRagSystem.Core.Interfaces;

public interface IConversationalQueryRefiner
{
    Task<QueryRefinementResult> RefineQueryAsync(
        string userMessage,
        ConversationState? conversationState
    );
}
