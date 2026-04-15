using Microsoft.EntityFrameworkCore;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using Urchin.Data;
using Urchin.Models;

using OllamaMessage = OllamaSharp.Models.Chat.Message;

namespace Urchin.Services;

public class ChatService : IChatService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly OllamaApiClient _ollama;
    private readonly AppState _appState;
    private readonly ILogger<ChatService> _logger;

    private const string SystemPrompt = "You are a helpful, concise assistant, taking the form of a technical urchin.";
    private const string ModelName    = "qwen3:8b";
    private const int    WindowSize   = 10;

    public ChatService(
        IDbContextFactory<AppDbContext> dbFactory,
        OllamaApiClient ollama,
        AppState appState,
        ILogger<ChatService> logger)
    {
        _dbFactory = dbFactory;
        _ollama    = ollama;
        _appState  = appState;
        _logger    = logger;
    }

    public async Task<List<Conversation>> GetConversationsAsync(string userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Conversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Urchin.Models.Message>> GetChatHistoryAsync(int conversationId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Timestamp)
            .ToListAsync();
    }

    public async Task<Conversation> CreateConversationAsync(string userId, string title)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var conversation = new Conversation
        {
            UserId = userId,
            Title = title,
            CreatedAt = DateTime.UtcNow
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        _logger.LogInformation("Created conversation {Id} for user {UserId}", conversation.Id, userId);
        return conversation;
    }

    public async Task DeleteConversationAsync(int conversationId, string userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);

        if (conversation == null)
            throw new InvalidOperationException("Conversation not found or access denied.");

        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync();
    }

    public async IAsyncEnumerable<string> GenerateResponseAsync(
        int conversationId, string userMessage, string userId)
    {
        // ── Phase 1: validate ownership, persist user message ────────────────
        bool isFirstMessage = false;

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var conv = await db.Conversations
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId);

            if (conv == null)
            {
                _logger.LogWarning(
                    "Unauthorized conversation access | ConvId={ConversationId} UserId={UserId}",
                    conversationId, userId);
                yield break;
            }

            isFirstMessage = conv.Messages.Count == 0;

            db.Messages.Add(new Urchin.Models.Message
            {
                ConversationId = conversationId,
                Role           = "user",
                Content        = userMessage,
                Timestamp      = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // ── Phase 2: load sliding-window history ─────────────────────────────
        List<Urchin.Models.Message> history;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            history = await db.Messages
                .Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.Timestamp)
                .Take(WindowSize)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();
        }

        // ── Phase 3: build Ollama chat and seed history ──────────────────────
        _ollama.SelectedModel = ModelName;
        var chat = new Chat(_ollama);

        chat.Messages.Add(new OllamaMessage(ChatRole.System, SystemPrompt));

        foreach (var m in history.Take(history.Count - 1))
        {
            chat.Messages.Add(new OllamaMessage(
                m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                m.Content));
        }

        // ── Phase 4: stream tokens ───────────────────────────────────────────
        var fullResponse = new System.Text.StringBuilder();
        var start        = DateTime.UtcNow;

        await foreach (var token in chat.SendAsync(userMessage))
        {
            if (!string.IsNullOrEmpty(token))
            {
                fullResponse.Append(token);
                yield return token;
            }
        }

        // ── Phase 5: persist assistant message ───────────────────────────────
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.Messages.Add(new Urchin.Models.Message
            {
                ConversationId = conversationId,
                Role           = "assistant",
                Content        = fullResponse.ToString(),
                Timestamp      = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        _logger.LogInformation(
            "Response complete | UserId={UserId} | ConvId={ConversationId} | ~{Tokens} tokens | {ElapsedMs}ms",
            userId, conversationId,
            fullResponse.Length / 4,
            (long)(DateTime.UtcNow - start).TotalMilliseconds);

        // ── Phase 6: generate title on first message ─────────────────────────
        if (isFirstMessage)
        {
            try
            {
                var title = await GenerateTitleAsync(userMessage);
                await using var db = await _dbFactory.CreateDbContextAsync();
                var conv = await db.Conversations.FindAsync(conversationId);
                if (conv != null)
                {
                    conv.Title = title;
                    await db.SaveChangesAsync();
                    _logger.LogInformation("Title generated for ConvId={ConversationId}: {Title}", conversationId, title);
                }
                _appState.NotifyConversationsChanged();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Title generation failed for ConvId={ConversationId}", conversationId);
            }
        }
    }

    // ── Title generation ─────────────────────────────────────────────────────
    private async Task<string> GenerateTitleAsync(string userMessage)
    {
        try
        {
            _ollama.SelectedModel = ModelName;
            var titleChat = new Chat(_ollama);
            titleChat.Messages.Add(new OllamaMessage(ChatRole.System,
                "You generate ultra-short chat titles. Respond with exactly 3-5 words, all lowercase, no punctuation, no quotes. Only the title, nothing else."));

            var snippet = userMessage.Length > 200 ? userMessage[..200] : userMessage;
            var sb      = new System.Text.StringBuilder();

            await foreach (var token in titleChat.SendAsync($"title for: {snippet}"))
                sb.Append(token);

            var title = sb.ToString().Trim().ToLower().TrimEnd('.', ',', '!', '?');
            return string.IsNullOrWhiteSpace(title) ? FallbackTitle(userMessage) : title;
        }
        catch
        {
            return FallbackTitle(userMessage);
        }
    }

    private static string FallbackTitle(string msg)
    {
        var words = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Take(5)).ToLower();
    }
}
