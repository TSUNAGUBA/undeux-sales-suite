using UndeuxSales.Core;
using UndeuxSales.Core.Rag;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Tests.Unit;

/// <summary>ChatContextService のメッセージ検証・検索クエリ組立（純粋ロジック）の単体テスト。</summary>
public sealed class ChatContextValidationTests
{
    private static AiChatMessage User(string content) => new("user", content);

    private static AiChatMessage Assistant(string content) => new("assistant", content);

    [Fact]
    public void ValidateAndTrim_EmptyMessages_Throws()
    {
        var ex = Assert.Throws<AppException>(
            () => ChatContextService.ValidateAndTrimMessages(Array.Empty<AiChatMessage>()));
        Assert.Equal(ErrorCodes.InvalidRequest.Code, ex.Error.Code);
    }

    [Fact]
    public void ValidateAndTrim_LastMessageMustBeUser()
    {
        var messages = new[] { User("こんにちは"), Assistant("どうされましたか") };
        Assert.Throws<AppException>(() => ChatContextService.ValidateAndTrimMessages(messages));
    }

    [Fact]
    public void ValidateAndTrim_RejectsInvalidRoleAndEmptyContent()
    {
        Assert.Throws<AppException>(
            () => ChatContextService.ValidateAndTrimMessages(new[] { new AiChatMessage("system", "x") }));
        Assert.Throws<AppException>(
            () => ChatContextService.ValidateAndTrimMessages(new[] { User("  ") }));
    }

    [Fact]
    public void ValidateAndTrim_RejectsOversizedMessage()
    {
        var messages = new[] { User(new string('あ', 4001)) };
        Assert.Throws<AppException>(() => ChatContextService.ValidateAndTrimMessages(messages));
    }

    [Fact]
    public void ValidateAndTrim_TrimsToRecentMessages_StartingWithUser()
    {
        var messages = new List<AiChatMessage>();
        for (var i = 0; i < 15; i++)
        {
            messages.Add(User($"質問{i}"));
            messages.Add(Assistant($"回答{i}"));
        }

        messages.Add(User("最後の質問"));

        var trimmed = ChatContextService.ValidateAndTrimMessages(messages);
        Assert.True(trimmed.Count <= 20);
        Assert.Equal("user", trimmed[0].Role);
        Assert.Equal("最後の質問", trimmed[^1].Content);
    }

    [Fact]
    public void BuildRetrievalQuery_UsesLatestUserMessage()
    {
        var query = ChatContextService.BuildRetrievalQuery(
            new[] { User("値札の取り付け方法を教えてください。ロットごとの注意点も知りたいです。") });
        Assert.Contains("値札の取り付け", query);
    }

    [Fact]
    public void BuildRetrievalQuery_ShortMessage_IncludesPreviousUserContext()
    {
        var query = ChatContextService.BuildRetrievalQuery(new[]
        {
            User("Web-EDIの数量確定のやり方を教えてください"),
            Assistant("数量確定はWeb-EDIのメニューから行います。"),
            User("はい"),
        });
        Assert.Contains("数量確定", query);
    }
}
