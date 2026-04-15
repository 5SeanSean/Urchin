using Urchin.Models;

namespace Urchin.Services;

public interface IChatService
{
    Task<Conversation> CreateConversationAsync(string userId, string title);
    Task<List<Conversation>> GetConversationsAsync(string userId);
    Task<List<Message>> GetChatHistoryAsync(int conversationId);
    IAsyncEnumerable<string> GenerateResponseAsync(int conversationId, string userMessage, string userId);
    Task DeleteConversationAsync(int conversationId, string userId);
}