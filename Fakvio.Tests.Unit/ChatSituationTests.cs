using Fakvio.UI.Shared.Components.Chat;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ChatSituation"/> — the client side of the situational context
/// (issue #230). These two functions decide what the chat request tells the server about
/// where the user is standing, so a wrong answer here silently misleads the assistant.
/// </summary>
public class ChatSituationTests
{
    [Theory]
    [InlineData("invoices/edit/42", "invoices/edit/42")]
    [InlineData("/invoices/edit/42/", "invoices/edit/42")]
    [InlineData("invoices?page=2&status=overdue", "invoices")]   // grid state tells the model nothing
    [InlineData("invoices/edit/42#items", "invoices/edit/42")]
    [InlineData("", null)]                                        // the dashboard is the app root
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizeRoute_KeepsThePathOnly(string? relativeUri, string? expected)
        => ChatSituation.NormalizeRoute(relativeUri).ShouldBe(expected);

    [Theory]
    [InlineData("invoices/edit/42", "invoices #42")]
    [InlineData("clients/7", "clients #7")]
    [InlineData("received-invoices/detail/1024", "received-invoices #1024")]
    public void DescribeOpenEntity_NamesTheRecordOnADetailRoute(string route, string expected)
        => ChatSituation.DescribeOpenEntity(route).ShouldBe(expected);

    [Theory]
    [InlineData("invoices")]        // list page — nothing open
    [InlineData("invoices/new")]    // a new document has no id yet
    [InlineData("42")]              // a bare number is not a record on a page
    [InlineData("")]
    [InlineData(null)]
    public void DescribeOpenEntity_WithoutARecordId_ReturnsNull(string? route)
        => ChatSituation.DescribeOpenEntity(route).ShouldBeNull();
}
