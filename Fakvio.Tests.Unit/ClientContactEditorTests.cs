using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Models;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class ClientContactEditorTests
{
    [Fact]
    public void UnrelatedEdit_PreservesEveryContactAndMetadata()
    {
        var original = new[]
        {
            new ContactDto { ContactType = EContactType.Email, ContactValue = "billing@example.test", Label = "Billing", IsPrimary = false },
            new ContactDto { ContactType = EContactType.Email, ContactValue = "owner@example.test", Label = "Owner", IsPrimary = true },
            new ContactDto { ContactType = EContactType.Phone, ContactValue = "+420111222333", Label = "Office" }
        };
        var result = ClientContactEditor.Merge(original, original[0].ContactValue, original[2].ContactValue);
        result.Count.ShouldBe(3);
        for (var i = 0; i < original.Length; i++)
        {
            result[i].ContactValue.ShouldBe(original[i].ContactValue);
            result[i].Label.ShouldBe(original[i].Label);
            result[i].IsPrimary.ShouldBe(original[i].IsPrimary);
        }
    }

    [Fact]
    public void EditingFirstEmail_DoesNotMutateSourceOrAdditionalEmail()
    {
        var original = new[]
        {
            new ContactDto { ContactType = EContactType.Email, ContactValue = "old@example.test", Label = "First", IsPrimary = true },
            new ContactDto { ContactType = EContactType.Email, ContactValue = "second@example.test", Label = "Second" }
        };
        var result = ClientContactEditor.Merge(original, "new@example.test", null);
        result[0].ContactValue.ShouldBe("new@example.test");
        result[0].Label.ShouldBe("First");
        result[0].IsPrimary.ShouldBe(true);
        result[1].ContactValue.ShouldBe("second@example.test");
        original[0].ContactValue.ShouldBe("old@example.test");
    }

    [Fact]
    public void ClearingVisibleEmail_RemovesOnlyThatContact()
    {
        var original = new[]
        {
            new ContactDto { ContactType = EContactType.Email, ContactValue = "first@example.test" },
            new ContactDto { ContactType = EContactType.Email, ContactValue = "second@example.test", IsPrimary = true }
        };
        var result = ClientContactEditor.Merge(original, " ", "+420111222333");
        result.Count.ShouldBe(2);
        result[0].ContactValue.ShouldBe("second@example.test");
        result[0].IsPrimary.ShouldBe(true);
        result[1].IsPrimary.ShouldBe(false);
    }
}
