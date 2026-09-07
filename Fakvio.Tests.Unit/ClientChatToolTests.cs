using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the four client chat tools added by issue #222:
/// <c>list_clients</c>, <c>get_client</c>, <c>update_client</c>, <c>delete_client</c>.
///
/// Two things are under test, per the issue's acceptance criteria:
/// 1. Parameter parsing — what the model sends must arrive at <see cref="IClientService"/>
///    unchanged in meaning (filters, update DTO, which client was picked).
/// 2. Error paths — every way the call can fail must produce a message the model can act on,
///    and must NOT write anything.
///
/// The confirm gate itself is NOT retested here: it lives in <c>ChatToolExecutor</c> and is
/// covered by <see cref="ChatToolExecutorTests"/>. What is tested here is the tools' half of
/// the contract — that <c>BuildPreviewAsync</c> writes nothing and describes the real change.
///
/// Junior note: <c>Substitute.For&lt;IClientService&gt;()</c> creates a fake service. Calls
/// to it are recorded, so a test can assert both what came back AND what the tool asked for.
/// </summary>
public class ClientChatToolTests
{
    private readonly IClientService _clientService = Substitute.For<IClientService>();

    private readonly ListClientsTool _list;
    private readonly GetClientTool _get;
    private readonly UpdateClientTool _update;
    private readonly DeleteClientTool _delete;

    /// <summary>Mirrors ListClientsTool.DefaultPageSize — the size the tool asks for by default.</summary>
    private const int DefaultPageSize = 10;

    /// <summary>Mirrors ClientLookup.MaxCandidatesShown — the cap on the "which one?" list.</summary>
    private const int MaxCandidatesShown = 10;

    /// <summary>Two candidates past the cap, so "… and N more" has a number worth asserting.</summary>
    private const int CandidatesOverTheCap = 12;

    public ClientChatToolTests()
    {
        _list = new ListClientsTool(_clientService, Substitute.For<ILogger<ListClientsTool>>());
        _get = new GetClientTool(_clientService, Substitute.For<ILogger<GetClientTool>>());
        _update = new UpdateClientTool(_clientService, Substitute.For<ILogger<UpdateClientTool>>());
        _delete = new DeleteClientTool(_clientService, Substitute.For<ILogger<DeleteClientTool>>());
    }

    // ─── Catalog ─────────────────────────────────────────────────────────────

    [Fact]
    public void ToolNames_AreTheOnesTheParityTablePromises()
    {
        _list.ToolName.ShouldBe("list_clients");
        _get.ToolName.ShouldBe("get_client");
        _update.ToolName.ShouldBe("update_client");
        _delete.ToolName.ShouldBe("delete_client");
    }

    [Fact]
    public void WriteTools_AreConfirmable_ReadToolsAreNot()
    {
        // The confirm gate only fires for IConfirmableChatTool. If update or delete ever loses
        // the interface, they would silently start writing on the first call.
        _update.ShouldBeAssignableTo<IConfirmableChatTool>();
        _delete.ShouldBeAssignableTo<IConfirmableChatTool>();

        _list.ShouldNotBeAssignableTo<IConfirmableChatTool>();
        _get.ShouldNotBeAssignableTo<IConfirmableChatTool>();
    }

    [Fact]
    public void UpdateSchema_ContainsIdentityAndChangeableFields_AndNoConfirmFlag()
    {
        var names = _update.Parameters.Select(parameter => parameter.Name).ToList();

        names.ShouldContain("id");
        names.ShouldContain("company_name");
        names.ShouldContain("refresh_from_ares");

        // Declaring 'confirm' yourself throws at startup — the executor appends it.
        names.ShouldNotContain(ChatToolConfirmation.ParameterName);

        // The IČO identifies which client to change; it is not itself changeable.
        names.ShouldNotContain("new_registration_number");
    }

    // ─── ClientLookup — shared by get / update / delete ──────────────────────

    [Fact]
    public async Task Get_Fails_WhenNoIdentifierWasSent()
    {
        var result = await _get.ExecuteAsync([]);

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("at least one of");
        await _clientService.DidNotReceiveWithAnyArgs().GetClientByIdAsync(default);
    }

    [Fact]
    public async Task Get_Fails_WhenBlankIdentifiersWereSent()
    {
        // A blank name must not become a substring that matches every client in the database.
        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["name"] = "   " });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("at least one of");
        await _clientService.DidNotReceiveWithAnyArgs().GetAllClientsAsync();
    }

    [Fact]
    public async Task Get_Fails_WhenIdIsNotNumeric()
    {
        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["id"] = "abc" });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Invalid id");
    }

    [Fact]
    public async Task Get_Fails_WhenIdIsUnknown()
    {
        _clientService.GetClientByIdAsync(7, Arg.Any<CancellationToken>()).Returns((ClientDto?)null);

        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["id"] = "7" });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("ID 7 not found");
    }

    [Fact]
    public async Task Get_PrefersId_OverRegistrationNumberAndName()
    {
        _clientService.GetClientByIdAsync(1, Arg.Any<CancellationToken>()).Returns(Client(1, "By ID"));

        var result = await _get.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "1",
            ["registration_number"] = "12345678",
            ["name"] = "Something else"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("By ID");

        // The less precise lookups must not even be attempted.
        await _clientService.DidNotReceiveWithAnyArgs().GetClientByRegistrationNumberAsync(default!);
        await _clientService.DidNotReceiveWithAnyArgs().GetAllClientsAsync();
    }

    [Theory]
    [InlineData(" 123 456 78 ")]        // dictated
    [InlineData("123 45678")]      // pasted from a document — non-breaking space
    [InlineData("1234	5678")]          // pasted from a spreadsheet — tab
    public async Task Get_StripsWhitespaceFromDictatedRegistrationNumber(string dictated)
    {
        _clientService.GetClientByRegistrationNumberAsync("12345678", Arg.Any<CancellationToken>())
            .Returns(Client(3, "Alfa s.r.o."));

        var result = await _get.ExecuteAsync(new Dictionary<string, string>
        {
            ["registration_number"] = dictated
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Alfa s.r.o.");
    }

    [Fact]
    public async Task Get_Fails_WhenRegistrationNumberIsUnknown()
    {
        _clientService.GetClientByRegistrationNumberAsync("99999999", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var result = await _get.ExecuteAsync(new Dictionary<string, string>
        {
            ["registration_number"] = "99999999"
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("99999999");
        result.OutputText.ShouldContain("not found");
    }

    [Fact]
    public async Task Get_MatchesNameCaseInsensitively_AndAlsoOnTradingName()
    {
        var client = Client(4, "Beta Holding a.s.");
        client.TradingName = "Gama Shop";
        _clientService.GetAllClientsAsync(true, Arg.Any<CancellationToken>())
            .Returns([Client(5, "Unrelated"), client]);

        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["name"] = "gama" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Beta Holding a.s.");
    }

    [Fact]
    public async Task Get_Fails_AndListsCandidates_WhenNameIsAmbiguous()
    {
        _clientService.GetAllClientsAsync(true, Arg.Any<CancellationToken>())
            .Returns([Client(1, "Alfa s.r.o."), Client(2, "Alfa Trade a.s.")]);

        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["name"] = "Alfa" });

        // Picking the first match would be a coin flip — for delete_client, a destructive one.
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("matches 2 clients");
        result.OutputText.ShouldContain("ID=1");
        result.OutputText.ShouldContain("ID=2");
        result.OutputText.ShouldContain("pass its id");
    }

    [Fact]
    public async Task Get_SearchesInactiveClientsToo_SoDeleteCanReportAlreadyDeleted()
    {
        var inactive = Client(9, "Zaniklá s.r.o.");
        inactive.IsActive = false;
        _clientService.GetAllClientsAsync(true, Arg.Any<CancellationToken>()).Returns([inactive]);

        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["name"] = "Zaniklá" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Active: No");
    }

    // ─── get_client — formatting ─────────────────────────────────────────────

    [Fact]
    public async Task Get_RendersEveryDetailSection()
    {
        var client = Client(11, "Detail s.r.o.");
        client.TaxNumber = "CZ12345678";
        client.Address = [new AddressDto
        {
            AddressType = EAddressType.Primary,
            Street = "Hlavní 1",
            City = "Praha",
            PostalCode = "11000",
            Country = "Czech Republic",
            IsPrimary = true
        }];
        client.Contact = [new ContactDto
        {
            ContactType = EContactType.Email,
            ContactValue = "fakturace@detail.cz",
            IsPrimary = true
        }];
        client.BankAccount = [new BankAccountDto
        {
            AccountNumber = "123456789/0800",
            BankName = "ČS",
            IsDefault = true
        }];
        client.BillingSettings = new BillingSettingsDto
        {
            DueDateCalculationType = EDueDateCalculationType.DaysFromIssue,
            DueDays = 21
        };

        _clientService.GetClientByIdAsync(11, Arg.Any<CancellationToken>()).Returns(client);

        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["id"] = "11" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("CZ12345678");
        result.OutputText.ShouldContain("Hlavní 1");
        result.OutputText.ShouldContain("fakturace@detail.cz");
        result.OutputText.ShouldContain("123456789/0800");
        result.OutputText.ShouldContain("21 days");
    }

    [Fact]
    public async Task Get_SaysNone_ForEmptySections()
    {
        // "No e-mail on file" is usually the answer the user is after — an omitted heading
        // reads to the model like data it forgot to fetch.
        _clientService.GetClientByIdAsync(12, Arg.Any<CancellationToken>()).Returns(Client(12, "Bare s.r.o."));

        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["id"] = "12" });

        result.OutputText.ShouldContain("Contacts:");
        result.OutputText.ShouldContain("(none)");
        result.OutputText.ShouldContain("company defaults apply");
    }

    // ─── list_clients — parameter parsing ────────────────────────────────────

    [Fact]
    public async Task List_UsesDefaults_WhenNoParametersWereSent()
    {
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([Client(1, "Alfa s.r.o.")]));

        var result = await _list.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Alfa s.r.o.");

        await _clientService.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(filter =>
                filter.Page == 1 &&
                filter.PageSize == 10 &&
                filter.Search == null &&
                filter.IsVatPayer == null &&
                filter.IsIssuer == null &&
                !filter.IncludeInactive),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_MapsEveryFilterOntoTheDto()
    {
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([]));

        await _list.ExecuteAsync(new Dictionary<string, string>
        {
            ["page"] = "3",
            ["page_size"] = "25",
            ["search"] = "  Alfa  ",
            ["is_vat_payer"] = "true",
            ["is_issuer"] = "false",
            ["include_inactive"] = "true"
        });

        await _clientService.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(filter =>
                filter.Page == 3 &&
                filter.PageSize == 25 &&
                filter.Search == "Alfa" &&
                filter.IsVatPayer == true &&
                filter.IsIssuer == false &&
                filter.IncludeInactive),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ReturnsTheIssuer_WhenIsIssuerIsTrue()
    {
        // This is the chat equivalent of the MCP GetIssuer tool — see DEVGUIDE §4.7.
        var issuer = Client(1, "Moje firma s.r.o.");
        issuer.IsIssuer = true;
        _clientService.GetClientsPagedAsync(
                Arg.Is<ClientFilterDto>(filter => filter.IsIssuer == true), Arg.Any<CancellationToken>())
            .Returns(Page([issuer]));

        var result = await _list.ExecuteAsync(new Dictionary<string, string> { ["is_issuer"] = "true" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Moje firma s.r.o.");
        result.OutputText.ShouldContain("ISSUER");
    }

    [Theory]
    [InlineData("500", 50)]   // above the cap → clamped
    [InlineData("0", 10)]     // nonsense → default
    [InlineData("-5", 10)]
    public async Task List_ClampsPageSize(string requested, int expected)
    {
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([]));

        await _list.ExecuteAsync(new Dictionary<string, string> { ["page_size"] = requested });

        await _clientService.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(filter => filter.PageSize == expected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_SaysSoExplicitly_WhenNothingMatches()
    {
        // An empty text block invites the model to invent rows; a sentence does not.
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([]));

        var result = await _list.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No clients match");
    }

    [Fact]
    public async Task List_ReportsPagingPosition_SoTheModelKnowsWhetherAnotherPageExists()
    {
        // The header is the model's only clue that rows are missing. Without it, "show me all
        // clients" gets answered from page 1 as if page 1 were everything.
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([Client(11, "Alfa s.r.o.")], pageNumber: 2, totalCount: 23));

        var result = await _list.ExecuteAsync(new Dictionary<string, string> { ["page"] = "2" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("page 2/3");
        result.OutputText.ShouldContain("total: 23");
    }

    [Fact]
    public async Task List_ReportsPageOneOfOne_WhenNothingMatches()
    {
        // PagedResult.TotalPages is 0 for an empty result; "page 1/0" reads to the model like
        // a page it failed to fetch, and invites a pointless retry.
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([], pageNumber: 1, totalCount: 0));

        var result = await _list.ExecuteAsync([]);

        result.OutputText.ShouldContain("page 1/1");
        result.OutputText.ShouldNotContain("/0");
    }

    [Theory]
    [InlineData("0")]        // below the first page
    [InlineData("-3")]
    [InlineData("abc")]      // the model spelled it out instead of sending a number
    [InlineData("")]
    public async Task List_FallsBackToTheFirstPage_WhenPageIsNotUsable(string requested)
    {
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([]));

        await _list.ExecuteAsync(new Dictionary<string, string> { ["page"] = requested });

        await _clientService.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(filter => filter.Page == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_DropsFiltersTheModelWordedInsteadOfSendingAsValues()
    {
        // Models answer a boolean with "yes" and fill a parameter they have no value for with
        // an empty string. Neither may narrow the result — an unparsable filter must read as
        // "no filter", and a blank search must not become the substring that matches everything.
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([]));

        await _list.ExecuteAsync(new Dictionary<string, string>
        {
            ["search"] = "   ",
            ["is_vat_payer"] = "yes",
            ["is_issuer"] = "1",
            ["include_inactive"] = "nope"
        });

        await _clientService.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(filter =>
                filter.Search == null &&
                filter.IsVatPayer == null &&
                filter.IsIssuer == null &&
                !filter.IncludeInactive),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_MarksOnlyTheIssuerRow_WhenTheUnfilteredPageHoldsBoth()
    {
        // The tool description promises the issuer is "flagged in its row" instead of getting
        // its own tool. If the flag were on every row (or on none), is_issuer=true would be the
        // only way to tell the user's own company from a customer.
        var issuer = Client(1, "Moje firma s.r.o.");
        issuer.IsIssuer = true;
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([issuer, Client(2, "Alfa s.r.o.")]));

        var result = await _list.ExecuteAsync([]);

        var issuerLine = LineContaining(result.OutputText, "Moje firma s.r.o.");
        var customerLine = LineContaining(result.OutputText, "Alfa s.r.o.");
        issuerLine.ShouldContain("ISSUER");
        customerLine.ShouldNotContain("ISSUER");
    }

    [Fact]
    public async Task List_MarksDeletedClientsAsInactive_WhenInactiveOnesWereAskedFor()
    {
        // include_inactive=true is how "which clients did we delete?" is answered. A row that
        // does not say so is indistinguishable from a live customer.
        var deleted = Client(7, "Zaniklá s.r.o.");
        deleted.IsActive = false;
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([deleted, Client(8, "Alfa s.r.o.")]));

        var result = await _list.ExecuteAsync(
            new Dictionary<string, string> { ["include_inactive"] = "true" });

        LineContaining(result.OutputText, "Zaniklá s.r.o.").ShouldContain("inactive");
        LineContaining(result.OutputText, "Alfa s.r.o.").ShouldNotContain("inactive");
    }

    [Fact]
    public async Task List_WritesNoneForMissingIdentifiers_InsteadOfLeavingThemBlank()
    {
        // An empty "IČO: " reads to the model like a value it truncated, and it will happily
        // repeat that as the client's IČO. A missing number has to say it is missing.
        var withoutNumbers = Client(3, "Fyzická osoba");
        withoutNumbers.RegistrationNumber = null;
        withoutNumbers.TaxNumber = null;
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(Page([withoutNumbers]));

        var result = await _list.ExecuteAsync([]);

        result.OutputText.ShouldContain("IČO: (none)");
        result.OutputText.ShouldContain("DIČ: (none)");
    }

    // ─── update_client ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePreview_DescribesEveryRequestedChange_AndWritesNothing()
    {
        var client = Client(20, "Staré jméno s.r.o.");
        client.TaxNumber = "CZ11111111";
        _clientService.GetClientByIdAsync(20, Arg.Any<CancellationToken>()).Returns(client);

        var preview = await _update.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["id"] = "20",
            ["company_name"] = "Nové jméno s.r.o.",
            ["is_vat_payer"] = "true"
        });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("Staré jméno s.r.o. → Nové jméno s.r.o.");
        preview.OutputText.ShouldContain("VAT payer: No → Yes");

        // Untouched fields must not appear as changes (the DIČ is only listed in the
        // client's description line above, never as an old → new pair).
        preview.OutputText.ShouldNotContain("DIČ: CZ11111111 →");

        await _clientService.DidNotReceiveWithAnyArgs().UpdateClientAsync(default, default!);
    }

    [Fact]
    public async Task UpdatePreview_ExplainsAresRefresh_WithoutPromisingValues()
    {
        var client = Client(21, "Alfa s.r.o.");
        client.RegistrationNumber = "12345678";
        _clientService.GetClientByIdAsync(21, Arg.Any<CancellationToken>()).Returns(client);

        var preview = await _update.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["id"] = "21",
            ["refresh_from_ares"] = "true"
        });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("ARES");
        preview.OutputText.ShouldContain("12345678");
        await _clientService.DidNotReceiveWithAnyArgs().UpdateClientAsync(default, default!);
    }

    [Fact]
    public async Task UpdatePreview_SaysExplicitFieldsBeatAres_WhenBothWereSent()
    {
        // ClientService writes the ARES data first and the explicit fields after it. Listing
        // both without saying so would read as a contradiction ("Name: A → B" and "Name will be
        // overwritten from ARES").
        var client = Client(28, "Alfa s.r.o.");
        _clientService.GetClientByIdAsync(28, Arg.Any<CancellationToken>()).Returns(client);

        var preview = await _update.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["id"] = "28",
            ["company_name"] = "Alfa Trade a.s.",
            ["refresh_from_ares"] = "true"
        });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("Alfa s.r.o. → Alfa Trade a.s.");
        preview.OutputText.ShouldContain("take precedence");
    }

    [Fact]
    public async Task Update_Fails_WhenNoChangeableFieldWasSent()
    {
        _clientService.GetClientByIdAsync(22, Arg.Any<CancellationToken>()).Returns(Client(22, "Alfa s.r.o."));

        var result = await _update.ExecuteAsync(new Dictionary<string, string> { ["id"] = "22" });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Nothing to change");
        result.OutputText.ShouldContain("company_name");
        await _clientService.DidNotReceiveWithAnyArgs().UpdateClientAsync(default, default!);
    }

    [Theory]
    [InlineData("is_vat_payer", "")]
    [InlineData("trading_name", "   ")]
    [InlineData("refresh_from_ares", "false")]
    public async Task Update_Fails_WhenTheOnlyFieldSentChangesNothing(string parameter, string value)
    {
        // ChatToolExecutor treats a blank value as "not supplied" but leaves the key in the
        // dictionary, and 'refresh_from_ares: false' is "leave it alone" — neither is a change,
        // so neither may be answered with "Client updated".
        _clientService.GetClientByIdAsync(29, Arg.Any<CancellationToken>()).Returns(Client(29, "Alfa s.r.o."));

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "29",
            [parameter] = value
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Nothing to change");
        await _clientService.DidNotReceiveWithAnyArgs().UpdateClientAsync(default, default!);
    }

    [Fact]
    public async Task Update_Fails_OnBlankCompanyName()
    {
        // UpdateClientDto's [StringLength(MinimumLength = 1)] is only enforced by API model
        // binding; a chat tool calls the service directly, so a blank name would be persisted.
        _clientService.GetClientByIdAsync(23, Arg.Any<CancellationToken>()).Returns(Client(23, "Alfa s.r.o."));

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "23",
            ["company_name"] = "   "
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("cannot be empty");
        await _clientService.DidNotReceiveWithAnyArgs().UpdateClientAsync(default, default!);
    }

    [Fact]
    public async Task Update_Fails_WhenAresRefreshIsAskedForAClientWithoutIco()
    {
        // ClientService would silently skip the refresh and report success — the user asked
        // for ARES data and must hear that it cannot be fetched.
        var client = Client(24, "Fyzická osoba");
        client.RegistrationNumber = null;
        _clientService.GetClientByIdAsync(24, Arg.Any<CancellationToken>()).Returns(client);

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "24",
            ["refresh_from_ares"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("no registration number");
        await _clientService.DidNotReceiveWithAnyArgs().UpdateClientAsync(default, default!);
    }

    [Fact]
    public async Task Update_MapsOnlySentFieldsOntoTheDto()
    {
        _clientService.GetClientByIdAsync(25, Arg.Any<CancellationToken>()).Returns(Client(25, "Alfa s.r.o."));
        _clientService.UpdateClientAsync(25, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(Client(25, "Alfa Trade a.s."));

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "25",
            ["company_name"] = " Alfa Trade a.s. ",
            ["is_active"] = "false"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Client updated");
        result.OutputText.ShouldContain("Alfa Trade a.s.");

        await _clientService.Received(1).UpdateClientAsync(
            25,
            Arg.Is<UpdateClientDto>(dto =>
                dto.CompanyName == "Alfa Trade a.s." &&
                dto.IsActive == false &&
                // Absent parameter must stay null — null means "don't change" in ClientService.
                dto.TradingName == null &&
                dto.TaxNumber == null &&
                dto.IsVatPayer == null &&
                !dto.RefreshFromAres),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UpdateSchema_ConstrainsTheDocumentLanguage_ToTheRenderableCodes()
    {
        // #306: the same ["cs", "en"] constraint update_my_company already declares. Without it
        // the model could send "de", which the application cannot render.
        var language = _update.Parameters.Single(parameter => parameter.Name == "language");

        language.AllowedValues.ShouldBe(["cs", "en"]);
    }

    [Fact]
    public async Task Update_PreviewsAndForwardsTheDocumentLanguage_Lowercased()
    {
        // ChatToolExecutor matches AllowedValues case-insensitively, so "EN" reaches the tool
        // as typed. It has to be lowercased here or the preview would promise a value that
        // ClientService then normalizes into something else.
        _clientService.GetClientByIdAsync(30, Arg.Any<CancellationToken>()).Returns(Client(30, "Alfa s.r.o."));
        _clientService.UpdateClientAsync(30, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(Client(30, "Alfa s.r.o."));

        var parameters = new Dictionary<string, string> { ["id"] = "30", ["language"] = "EN" };

        var preview = await _update.BuildPreviewAsync(parameters);
        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("Document language: cs → en");

        var result = await _update.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeTrue();
        await _clientService.Received(1).UpdateClientAsync(
            30,
            Arg.Is<UpdateClientDto>(dto => dto.Language == "en"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_TreatsRefreshFromAresFalse_AsNoRefresh()
    {
        var client = Client(26, "Alfa s.r.o.");
        _clientService.GetClientByIdAsync(26, Arg.Any<CancellationToken>()).Returns(client);
        _clientService.UpdateClientAsync(26, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(client);

        await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "26",
            ["trading_name"] = "Alfa",
            ["refresh_from_ares"] = "false"
        });

        await _clientService.Received(1).UpdateClientAsync(
            26, Arg.Is<UpdateClientDto>(dto => !dto.RefreshFromAres), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_Fails_WhenAresWasAskedFor_ButTheRegistryReturnedNothing()
    {
        // ClientService overwrites the fields ONLY when ARES answers, and stamps LastAresFetchDate
        // in the same branch. When the registry is down it saves the rest and returns a DTO — so
        // reporting "Client updated" here would hand the user stale data with a confirmation.
        var client = Client(40, "Alfa s.r.o.");
        _clientService.GetClientByIdAsync(40, Arg.Any<CancellationToken>()).Returns(client);
        _clientService.UpdateClientAsync(40, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(Client(40, "Alfa s.r.o."));   // LastAresFetchDate still null = no fetch happened

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "40",
            ["refresh_from_ares"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("NOT refreshed");
        result.OutputText.ShouldContain("12345678");
        result.OutputText.ShouldContain("Nothing else was changed");
    }

    [Fact]
    public async Task Update_SaysWhichHalfSurvived_WhenAresFailedButExplicitFieldsWereSaved()
    {
        // The explicit fields really were written, so "nothing happened" would be the opposite
        // lie. The message has to separate the two halves.
        var client = Client(41, "Alfa s.r.o.");
        _clientService.GetClientByIdAsync(41, Arg.Any<CancellationToken>()).Returns(client);
        _clientService.UpdateClientAsync(41, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(Client(41, "Alfa Trade a.s."));

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "41",
            ["company_name"] = "Alfa Trade a.s.",
            ["refresh_from_ares"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("NOT refreshed");
        result.OutputText.ShouldContain("sent explicitly were saved");
        result.OutputText.ShouldContain("Alfa Trade a.s.");
    }

    [Fact]
    public async Task Update_ReportsSuccess_WhenAresActuallyAnswered()
    {
        // The counterpart of the two tests above: a bumped LastAresFetchDate is the service's
        // proof that the fetch succeeded, and then the success message is the honest one.
        var client = Client(42, "Alfa s.r.o.");
        _clientService.GetClientByIdAsync(42, Arg.Any<CancellationToken>()).Returns(client);

        var refreshed = Client(42, "Alfa Trade a.s.");
        refreshed.LastAresFetchDate = DateTime.UtcNow;
        _clientService.UpdateClientAsync(42, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(refreshed);

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "42",
            ["refresh_from_ares"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Client updated");
        result.OutputText.ShouldContain("Alfa Trade a.s.");
    }

    [Fact]
    public async Task Update_Fails_WhenTheClientVanishedBetweenLookupAndWrite()
    {
        _clientService.GetClientByIdAsync(27, Arg.Any<CancellationToken>()).Returns(Client(27, "Alfa s.r.o."));
        _clientService.UpdateClientAsync(27, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "27",
            ["company_name"] = "Nové jméno"
        });

        // The model must not report a change that did not happen.
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("no longer exists");
    }

    [Fact]
    public async Task Update_IgnoresTheConfirmFlag_WhenReadingItsOwnParameters()
    {
        // The executor does not strip 'confirm' — it reaches the tool and must not be mistaken
        // for a field to change.
        _clientService.GetClientByIdAsync(28, Arg.Any<CancellationToken>()).Returns(Client(28, "Alfa s.r.o."));

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "28",
            [ChatToolConfirmation.ParameterName] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Nothing to change");
    }

    // ─── delete_client ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeletePreview_NamesTheClient_SaysItIsSoft_AndWritesNothing()
    {
        _clientService.GetClientByIdAsync(30, Arg.Any<CancellationToken>()).Returns(Client(30, "Alfa s.r.o."));

        var preview = await _delete.BuildPreviewAsync(new Dictionary<string, string> { ["id"] = "30" });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("Alfa s.r.o.");
        preview.OutputText.ShouldContain("deactivated, not erased");
        await _clientService.DidNotReceiveWithAnyArgs().DeleteClientAsync(default);
    }

    [Fact]
    public async Task DeletePreview_Fails_WhenTheClientIsAlreadyInactive()
    {
        var client = Client(31, "Zaniklá s.r.o.");
        client.IsActive = false;
        _clientService.GetClientByIdAsync(31, Arg.Any<CancellationToken>()).Returns(client);

        var preview = await _delete.BuildPreviewAsync(new Dictionary<string, string> { ["id"] = "31" });

        // A failed preview is not offered for confirmation — the user's turn is not wasted.
        preview.IsSuccess.ShouldBeFalse();
        preview.OutputText.ShouldContain("already deleted");
    }

    [Fact]
    public async Task Delete_DeactivatesTheClient()
    {
        _clientService.GetClientByIdAsync(32, Arg.Any<CancellationToken>()).Returns(Client(32, "Alfa s.r.o."));
        _clientService.DeleteClientAsync(32, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _delete.ExecuteAsync(new Dictionary<string, string> { ["id"] = "32" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("deleted (deactivated)");
        result.OutputText.ShouldContain("Alfa s.r.o.");
    }

    [Fact]
    public async Task Delete_Fails_WhenTheClientWasDeletedBetweenPreviewAndConfirmation()
    {
        // DeleteClientAsync returns true for an already-inactive client, so the tool has to
        // notice the no-op itself instead of announcing a delete that deleted nothing.
        var client = Client(35, "Zaniklá s.r.o.");
        client.IsActive = false;
        _clientService.GetClientByIdAsync(35, Arg.Any<CancellationToken>()).Returns(client);

        var result = await _delete.ExecuteAsync(new Dictionary<string, string> { ["id"] = "35" });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("already deleted");
        await _clientService.DidNotReceiveWithAnyArgs().DeleteClientAsync(default);
    }

    [Fact]
    public async Task Delete_ReportsTheBusinessRule_WhenTheClientHasInvoices()
    {
        _clientService.GetClientByIdAsync(33, Arg.Any<CancellationToken>()).Returns(Client(33, "Alfa s.r.o."));
        _clientService.DeleteClientAsync(33, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cannot delete client with existing invoices"));

        var result = await _delete.ExecuteAsync(new Dictionary<string, string> { ["id"] = "33" });

        // Reported verbatim so the model can explain WHY, not just "it failed".
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("existing invoices");
    }

    [Fact]
    public async Task Delete_Fails_WhenTheClientVanishedBetweenLookupAndWrite()
    {
        _clientService.GetClientByIdAsync(34, Arg.Any<CancellationToken>()).Returns(Client(34, "Alfa s.r.o."));
        _clientService.DeleteClientAsync(34, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _delete.ExecuteAsync(new Dictionary<string, string> { ["id"] = "34" });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("no longer exists");
    }

    // ─── Ambiguity is a write barrier, not a formatting detail ───────────────

    [Fact]
    public async Task Lookup_CapsTheCandidateList_WhenTheNameMatchesTooMany()
    {
        // A hundred rows would push the real answer out of the model's context. The cap has to
        // say how many were hidden, or "matches 12" and a list of 10 contradict each other.
        var matches = Enumerable.Range(1, CandidatesOverTheCap)
            .Select(index => Client(index, $"Alfa {index} s.r.o."))
            .ToList();
        _clientService.GetAllClientsAsync(true, Arg.Any<CancellationToken>()).Returns(matches);

        var result = await _get.ExecuteAsync(new Dictionary<string, string> { ["name"] = "Alfa" });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain($"matches {CandidatesOverTheCap} clients");
        CountOccurrences(result.OutputText, "ID=").ShouldBe(MaxCandidatesShown);
        result.OutputText.ShouldContain($"and {CandidatesOverTheCap - MaxCandidatesShown} more");
    }

    [Fact]
    public async Task Update_WritesNothing_WhenTheNameMatchesMoreThanOneClient()
    {
        // Renaming the wrong company is data loss, so an ambiguous name must stop before the
        // write — not resolve to the first match the database happened to return.
        _clientService.GetAllClientsAsync(true, Arg.Any<CancellationToken>())
            .Returns([Client(1, "Alfa s.r.o."), Client(2, "Alfa Trade a.s.")]);

        var result = await _update.ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "Alfa",
            ["company_name"] = "Alfa Group a.s."
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("matches 2 clients");
        await _clientService.DidNotReceiveWithAnyArgs().UpdateClientAsync(default, default!);
    }

    [Fact]
    public async Task Delete_WritesNothing_WhenTheNameMatchesMoreThanOneClient()
    {
        _clientService.GetAllClientsAsync(true, Arg.Any<CancellationToken>())
            .Returns([Client(1, "Alfa s.r.o."), Client(2, "Alfa Trade a.s.")]);

        var result = await _delete.ExecuteAsync(new Dictionary<string, string> { ["name"] = "Alfa" });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("matches 2 clients");
        await _clientService.DidNotReceiveWithAnyArgs().DeleteClientAsync(default);
    }

    [Fact]
    public async Task DeletePreview_WritesNothing_WhenTheNameMatchesMoreThanOneClient()
    {
        // The preview is what the user says "yes" to. If it silently picked one of the two,
        // the confirmation would be for a company the user never saw named.
        _clientService.GetAllClientsAsync(true, Arg.Any<CancellationToken>())
            .Returns([Client(1, "Alfa s.r.o."), Client(2, "Alfa Trade a.s.")]);

        var result = await _delete.BuildPreviewAsync(new Dictionary<string, string> { ["name"] = "Alfa" });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("matches 2 clients");
        await _clientService.DidNotReceiveWithAnyArgs().DeleteClientAsync(default);
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────

    /// <summary>Minimal active customer — tests override only the field they are about.</summary>
    private static ClientDto Client(long id, string companyName) => new()
    {
        Id = id,
        CompanyName = companyName,
        RegistrationNumber = "12345678",
        IsActive = true
    };

    private static PagedResult<ClientDto> Page(List<ClientDto> items)
        => Page(items, pageNumber: 1, totalCount: items.Count);

    /// <summary>Paged result with a caller-chosen position, for asserting the paging header.</summary>
    private static PagedResult<ClientDto> Page(List<ClientDto> items, int pageNumber, int totalCount) => new()
    {
        Items = items,
        TotalCount = totalCount,
        PageNumber = pageNumber,
        PageSize = DefaultPageSize
    };

    /// <summary>
    /// The one output line mentioning <paramref name="needle"/>. Lets a test assert what a
    /// single client's row says without depending on the order of the rows around it.
    /// </summary>
    private static string LineContaining(string output, string needle)
        => output.Split(Environment.NewLine).Single(line => line.Contains(needle));

    private static int CountOccurrences(string text, string needle)
        => text.Split(needle).Length - 1;
}
