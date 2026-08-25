using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the five company-profile chat tools (issue #220):
///   - GetMyCompanyTool        (read-only)
///   - UpdateMyCompanyTool     (confirmable write)
///   - AddBankAccountTool      (confirmable write)
///   - UpdateBankAccountTool   (confirmable write)
///   - DeleteBankAccountTool   (confirmable write)
///
/// Everything is mocked with NSubstitute — no database. What is verified is what the tools own:
/// parameter parsing, the DTO they hand to <see cref="IClientService"/>, the error paths, and
/// that a preview really writes nothing.
///
/// Junior note: the confirm gate itself lives in ChatToolExecutor and is tested in
/// <see cref="ChatToolExecutorTests"/>. Here the two halves are called directly —
/// <c>BuildPreviewAsync</c> is what the executor calls without <c>confirm: true</c>,
/// <c>ExecuteAsync</c> is what it calls with it.
/// </summary>
public class CompanySettingsChatToolTests
{
    // ─── Shared builders ──────────────────────────────────────────────────

    private static BankAccountDto BuildAccount(
        long id = 1,
        string accountNumber = "1234567890/0100",
        string? label = "CZK účet",
        string? currencyCode = "CZK",
        bool isDefault = true)
        => new()
        {
            Id = id,
            AccountNumber = accountNumber,
            Label = label,
            BankName = "Fio banka",
            CurrencyCode = currencyCode,
            IsDefault = isDefault
        };

    private static ClientDto BuildIssuer(params BankAccountDto[] accounts)
        => new()
        {
            Id = 7,
            CompanyName = "Fakvio s.r.o.",
            RegistrationNumber = "12345678",
            TaxNumber = "CZ12345678",
            IsVatPayer = true,
            IsIssuer = true,
            Language = "cs",
            Address =
            [
                new AddressDto
                {
                    Id = 11,
                    AddressType = EAddressType.Primary,
                    Street = "Národní 1",
                    City = "Praha",
                    PostalCode = "11000",
                    Country = "Česká republika",
                    AddressLine2 = "2. patro",
                    IsPrimary = true
                }
            ],
            Contact =
            [
                new ContactDto
                {
                    Id = 21,
                    ContactType = EContactType.Email,
                    ContactValue = "fakturace@fakvio.cz",
                    IsPrimary = true
                }
            ],
            BankAccount = [.. accounts]
        };

    /// <summary>
    /// A client service stub that reports the given issuer and echoes every update back as a
    /// successful save. Individual tests override the pieces they care about.
    /// </summary>
    private static IClientService StubService(ClientDto? issuer)
    {
        var service = Substitute.For<IClientService>();
        service.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(issuer);
        service.UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(issuer);
        service.AddBankAccountAsync(Arg.Any<long>(), Arg.Any<CreateBankAccountDto>(), Arg.Any<CancellationToken>())
            .Returns(issuer);
        return service;
    }

    private static GetMyCompanyTool CreateGetTool(IClientService service)
        => new(service, Substitute.For<ILogger<GetMyCompanyTool>>());

    private static UpdateMyCompanyTool CreateUpdateCompanyTool(IClientService service)
        => new(service, Substitute.For<ILogger<UpdateMyCompanyTool>>());

    private static AddBankAccountTool CreateAddAccountTool(IClientService service)
        => new(service, Substitute.For<ILogger<AddBankAccountTool>>());

    private static UpdateBankAccountTool CreateUpdateAccountTool(IClientService service)
        => new(service, Substitute.For<ILogger<UpdateBankAccountTool>>());

    private static DeleteBankAccountTool CreateDeleteAccountTool(IClientService service)
        => new(service, Substitute.For<ILogger<DeleteBankAccountTool>>());

    /// <summary>Pulls the DTO the tool handed to UpdateClientAsync.</summary>
    private static UpdateClientDto CapturedUpdate(IClientService service)
        => (UpdateClientDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IClientService.UpdateClientAsync))
            .GetArguments()[1]!;

    /// <summary>Pulls the DTO the tool handed to AddBankAccountAsync.</summary>
    private static CreateBankAccountDto CapturedAdd(IClientService service)
        => (CreateBankAccountDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IClientService.AddBankAccountAsync))
            .GetArguments()[1]!;

    /// <summary>
    /// The foreign-key failure the database raises when a re-inserted account is already
    /// referenced by payment data (FK <c>Restrict</c>). Built in one place because three tests
    /// need the exact same shape — <c>DbUpdateException</c> wrapping a SQLSTATE 23503
    /// <c>PostgresException</c> — and only that shape is supposed to produce the "payment data"
    /// wording.
    /// </summary>
    private static DbUpdateException ForeignKeyViolation()
        => new(
            "An error occurred while saving the entity changes.",
            new PostgresException(
                "update or delete on table \"BankAccount\" violates foreign key constraint " +
                "\"FK_BankTransaction_BankAccount_BankAccountId\" on table \"tenant_7.BankTransaction\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.ForeignKeyViolation));

    /// <summary>Id of the first re-inserted account in <see cref="ReplayBankAccountSave"/>.</summary>
    private const long ReinsertedAccountId = 100;

    /// <summary>
    /// Makes the stub replay what ClientService actually does on a replace-all save of the bank
    /// accounts: it wipes the collection and re-inserts the supplied one with fresh ids, and when
    /// no account arrives flagged as default it promotes the one at index 0
    /// (<c>hasExplicitDefault ? … : index == 0</c> in <c>ClientService.UpdateClientAsync</c>).
    ///
    /// The plain echo stub cannot answer "which account is the default now", because it hands
    /// back the accounts the tool started from instead of the ones it saved.
    /// </summary>
    private static void ReplayBankAccountSave(IClientService service, ClientDto issuer)
        => service.UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var saved = ((UpdateClientDto)call[1]).BankAccount;
                if (saved is null)
                    return issuer;

                var hasExplicitDefault = saved.Any(account => account.IsDefault == true);

                // A separate instance on purpose. ClientService re-reads the client after the
                // save, so the object the tool started from keeps the pre-save accounts — and a
                // tool that answers from that stale object has to be visible as a failure here.
                return new ClientDto
                {
                    Id = issuer.Id,
                    CompanyName = issuer.CompanyName,
                    BankAccount = saved
                        .Select((account, index) => new BankAccountDto
                        {
                            Id = ReinsertedAccountId + index,
                            AccountNumber = account.AccountNumber ?? string.Empty,
                            Label = account.Label,
                            BankName = account.BankName,
                            IBAN = account.IBAN,
                            SWIFT = account.SWIFT,
                            CurrencyCode = account.CurrencyCode,
                            IsDefault = hasExplicitDefault ? account.IsDefault == true : index == 0
                        })
                        .ToList()
                };
            });

    /// <summary>
    /// The one rendered line that mentions the given account number. The tools print one account
    /// per line, so this is how a test asks "what does the answer say about THIS account".
    /// </summary>
    private static string LineFor(string? renderedText, string accountNumber)
        => renderedText!.Split('\n').Single(line => line.Contains(accountNumber));

    // ═══════════════════════════════════════════════════════════════════════
    //  GetMyCompanyTool
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A tenant has exactly one issuer, so the tool takes nothing — an empty schema is what
    /// tells the model it can just call it. It must also stay read-only (not confirmable).
    /// </summary>
    [Fact]
    public void GetMyCompanyTool_IsReadOnlyAndTakesNoParameters()
    {
        var tool = CreateGetTool(StubService(BuildIssuer()));

        tool.ToolName.ShouldBe("get_my_company");
        tool.Parameters.ShouldBeEmpty();
        tool.ShouldNotBeAssignableTo<IConfirmableChatTool>();
    }

    /// <summary>
    /// The output is the model's only source for the answer, so every field the user may ask
    /// about has to be in it — including the bank account IDs the write tools need as a handle.
    /// </summary>
    [Fact]
    public async Task GetMyCompanyTool_FormatsProfileIncludingBankAccountIds()
    {
        var issuer = BuildIssuer(BuildAccount(id: 3, accountNumber: "999/0800"));
        var tool = CreateGetTool(StubService(issuer));

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Fakvio s.r.o.");
        result.OutputText.ShouldContain("12345678");
        result.OutputText.ShouldContain("CZ12345678");
        result.OutputText.ShouldContain("VAT payer: yes");
        result.OutputText.ShouldContain("Národní 1");
        result.OutputText.ShouldContain("fakturace@fakvio.cz");
        result.OutputText.ShouldContain("ID=3");
        result.OutputText.ShouldContain("999/0800");
    }

    /// <summary>
    /// A company with no bank account yet must be reported as such, not omitted — otherwise the
    /// model answers "your account number is …" from imagination.
    /// </summary>
    [Fact]
    public async Task GetMyCompanyTool_WithoutBankAccounts_SaysSoExplicitly()
    {
        var tool = CreateGetTool(StubService(BuildIssuer()));

        var result = await tool.ExecuteAsync([]);

        result.OutputText.ShouldContain("Bank accounts: none yet");
    }

    /// <summary>
    /// A tenant that never filled in its company has no issuer at all. All five tools fail the
    /// same way and name the page where the user fixes it.
    /// </summary>
    [Fact]
    public async Task GetMyCompanyTool_WithoutIssuer_FailsWithSetupHint()
    {
        var tool = CreateGetTool(StubService(issuer: null));

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("/my-company");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UpdateMyCompanyTool
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The tool changes data, so the executor must be able to gate it. It must also not declare
    /// the reserved 'confirm' parameter itself — that would throw at startup.
    /// </summary>
    [Fact]
    public void UpdateMyCompanyTool_IsConfirmableAndDoesNotDeclareConfirm()
    {
        var tool = CreateUpdateCompanyTool(StubService(BuildIssuer()));

        tool.ToolName.ShouldBe("update_my_company");
        tool.ShouldBeAssignableTo<IConfirmableChatTool>();
        tool.Parameters.ShouldNotContain(parameter => parameter.Name == "confirm");
    }

    /// <summary>
    /// The whole point of the confirm gate: the preview describes the change and writes nothing.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_Preview_DescribesChangeAndWritesNothing()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateUpdateCompanyTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["company_name"] = "Fakvio a.s."
        });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("Fakvio s.r.o.");
        preview.OutputText.ShouldContain("Fakvio a.s.");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Only the supplied fields may travel in the DTO — every null field means "leave it alone"
    /// for ClientService, so a field the user never mentioned must stay null.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_SendsOnlyTheSuppliedFields()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateUpdateCompanyTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["company_name"] = "  Fakvio a.s.  ",
            ["is_vat_payer"] = "false",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var update = CapturedUpdate(service);
        update.CompanyName.ShouldBe("Fakvio a.s.");   // trimmed
        update.IsVatPayer.ShouldBe(false);
        update.TaxNumber.ShouldBeNull();
        update.TradingName.ShouldBeNull();
        update.Language.ShouldBeNull();
        update.Address.ShouldBeNull();
        update.BankAccount.ShouldBeNull();
    }

    /// <summary>
    /// Sending a value the company already has is not a change. Reporting it as one would tell
    /// the user something happened when nothing did.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_ValueEqualToStoredOne_ChangesNothing()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateUpdateCompanyTool(service);

        // "CS" vs the stored "cs": the language comparison is case-insensitive on purpose.
        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["company_name"] = "Fakvio s.r.o.",
            ["language"] = "CS"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("No change was requested");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A call with no field at all (the model just echoing the confirm flag) must be refused
    /// rather than saved as an empty update.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_WithoutAnyField_IsRefused()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateUpdateCompanyTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string> { ["confirm"] = "true" });

        preview.IsSuccess.ShouldBeFalse();
        preview.ErrorMessage.ShouldContain("No change was requested");
    }

    /// <summary>
    /// Addresses are replaced as a whole by ClientService, so one changed field has to be sent
    /// together with everything else the address already had — otherwise the rest is erased.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_ChangingOneAddressField_KeepsTheRestOfTheAddress()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateUpdateCompanyTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["city"] = "Brno" });

        result.IsSuccess.ShouldBeTrue();

        var address = CapturedUpdate(service).Address!.ShouldHaveSingleItem();
        address.City.ShouldBe("Brno");
        address.Street.ShouldBe("Národní 1");
        address.PostalCode.ShouldBe("11000");
        address.Country.ShouldBe("Česká republika");
        address.AddressLine2.ShouldBe("2. patro");
        address.IsPrimary.ShouldBe(true);
    }

    /// <summary>
    /// A company with no address on file gets one created from whatever the user supplied —
    /// the fields they did not mention stay empty rather than blocking the change.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_WithoutStoredAddress_CreatesThePrimaryOne()
    {
        var issuer = BuildIssuer();
        issuer.Address.Clear();

        var service = StubService(issuer);
        var tool = CreateUpdateCompanyTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["street"] = "Nová 5",
            ["city"] = "Ostrava"
        });

        result.IsSuccess.ShouldBeTrue();

        var address = CapturedUpdate(service).Address!.ShouldHaveSingleItem();
        address.Street.ShouldBe("Nová 5");
        address.City.ShouldBe("Ostrava");
        address.PostalCode.ShouldBe(string.Empty);
        address.IsPrimary.ShouldBe(true);
    }

    /// <summary>
    /// Secondary addresses must survive an edit of the primary one — they are part of the same
    /// replaced collection.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_ChangingPrimaryAddress_KeepsSecondaryAddresses()
    {
        var issuer = BuildIssuer();
        issuer.Address.Add(new AddressDto
        {
            Id = 12,
            AddressType = EAddressType.Shipping,
            Street = "Skladová 9",
            City = "Kladno",
            PostalCode = "27201",
            Country = "Česká republika"
        });

        var service = StubService(issuer);
        var tool = CreateUpdateCompanyTool(service);

        await tool.ExecuteAsync(new Dictionary<string, string> { ["street"] = "Nová 5" });

        var addresses = CapturedUpdate(service).Address!;
        addresses.Count.ShouldBe(2);
        addresses[0].Street.ShouldBe("Nová 5");
        addresses[0].IsPrimary.ShouldBe(true);
        addresses[1].Street.ShouldBe("Skladová 9");
        addresses[1].City.ShouldBe("Kladno");
        addresses[1].IsPrimary.ShouldBe(false);
    }

    /// <summary>
    /// The company may disappear between the preview and the confirmed call — the tools re-read
    /// it every time and report the miss instead of throwing.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_WithoutIssuer_Fails()
    {
        var tool = CreateUpdateCompanyTool(StubService(issuer: null));

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["company_name"] = "X" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("/my-company");
    }

    /// <summary>
    /// ClientService returns null when the client vanished mid-flight. That is a failure, not a
    /// silent success — the model must not report a change that did not happen.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_WhenSaveReportsMissingClient_Fails()
    {
        var service = StubService(BuildIssuer());
        service.UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var tool = CreateUpdateCompanyTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["company_name"] = "X" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("no longer exists");
    }

    /// <summary>
    /// Flipping the VAT payer flag decides whether the next invoice carries VAT at all, so the
    /// preview has to spell out the direction — "VAT payer: yes" alone reads the same whether it
    /// is the old value or the new one. Both directions are checked because a swapped pair of
    /// values would still produce a sentence that looks right.
    /// </summary>
    [Theory]
    [InlineData(false, "true", "VAT payer: no → yes")]
    [InlineData(true, "false", "VAT payer: yes → no")]
    public async Task UpdateMyCompanyTool_VatPayerFlag_IsPreviewedWithTheDirectionOfTheChange(
        bool storedFlag,
        string requestedFlag,
        string expectedChangeLine)
    {
        var issuer = BuildIssuer();
        issuer.IsVatPayer = storedFlag;

        var service = StubService(issuer);
        var tool = CreateUpdateCompanyTool(service);
        var parameters = new Dictionary<string, string> { ["is_vat_payer"] = requestedFlag };

        var preview = await tool.BuildPreviewAsync(parameters);
        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain(expectedChangeLine);

        parameters["confirm"] = "true";
        var result = await tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeTrue();
        CapturedUpdate(service).IsVatPayer.ShouldBe(!storedFlag);
    }

    /// <summary>
    /// "Jsme plátci DPH" about a company that already is one is not a change. Reporting it as one
    /// would tell the user their VAT status has just been switched when nothing happened.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_VatPayerFlagRepeatingTheStoredValue_IsRefused()
    {
        var service = StubService(BuildIssuer());   // the stored company is a VAT payer
        var tool = CreateUpdateCompanyTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["is_vat_payer"] = "true",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("No change was requested");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The model likes to echo back the address it was just shown. Rebuilt, that address is
    /// identical to the stored one — and rewriting the collection for an identical result would
    /// hand the addresses new ids and report a move that never happened.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_AddressRepeatingTheStoredValue_IsRefused()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateUpdateCompanyTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["street"] = "Národní 1",
            ["city"] = "Praha",
            ["postal_code"] = "11000",
            ["country"] = "Česká republika",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("No change was requested");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Older data can hold a single address with no primary flag on it. That address is still the
    /// company's address, so it gets rewritten — and flagged primary — instead of being left
    /// behind next to a newly created one.
    /// </summary>
    [Fact]
    public async Task UpdateMyCompanyTool_AddressNotFlaggedPrimary_IsRewrittenNotDuplicated()
    {
        var issuer = BuildIssuer();
        issuer.Address[0].IsPrimary = false;

        var service = StubService(issuer);
        var tool = CreateUpdateCompanyTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["city"] = "Brno",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var address = CapturedUpdate(service).Address!.ShouldHaveSingleItem();
        address.City.ShouldBe("Brno");
        address.Street.ShouldBe("Národní 1");   // carried over from the unflagged address
        address.IsPrimary.ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AddBankAccountTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void AddBankAccountTool_IsConfirmableAndRequiresTheAccountNumber()
    {
        var tool = CreateAddAccountTool(StubService(BuildIssuer()));

        tool.ToolName.ShouldBe("add_bank_account");
        tool.ShouldBeAssignableTo<IConfirmableChatTool>();
        tool.Parameters.Single(parameter => parameter.Name == "account_number").IsRequired.ShouldBeTrue();
        tool.Parameters.Count(parameter => parameter.IsRequired).ShouldBe(1);
    }

    /// <summary>
    /// The first account of a company silently becomes the default one (ClientService does it),
    /// so the preview has to say so — it is the one consequence the user cannot see coming.
    /// </summary>
    [Fact]
    public async Task AddBankAccountTool_Preview_FirstAccountBecomesDefault_AndWritesNothing()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateAddAccountTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["account_number"] = "555/0300"
        });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("555/0300");
        preview.OutputText.ShouldContain("DEFAULT");
        await service.DidNotReceive().AddBankAccountAsync(
            Arg.Any<long>(), Arg.Any<CreateBankAccountDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Taking the default flag away from an existing account is a change of its own, so the
    /// preview names the account that would lose it.
    /// </summary>
    [Fact]
    public async Task AddBankAccountTool_Preview_NamesTheDefaultAccountItWouldReplace()
    {
        var service = StubService(BuildIssuer(BuildAccount(id: 1, accountNumber: "111/0100")));
        var tool = CreateAddAccountTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["account_number"] = "222/0300",
            ["is_default"] = "true"
        });

        preview.OutputText.ShouldContain("replacing '111/0100'");
    }

    /// <summary>
    /// Every optional parameter must reach the DTO, trimmed, with the currency uppercased —
    /// this is the parameter parsing the acceptance criteria asks about.
    /// </summary>
    [Fact]
    public async Task AddBankAccountTool_MapsEveryParameterOntoTheDto()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateAddAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["account_number"] = " 555/0300 ",
            ["label"] = "EUR účet",
            ["bank_name"] = "ČSOB",
            ["iban"] = "CZ6508000000192000014565",
            ["swift"] = "GIBACZPX",
            ["currency_code"] = "eur",
            ["is_default"] = "true",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var created = CapturedAdd(service);
        created.AccountNumber.ShouldBe("555/0300");
        created.Label.ShouldBe("EUR účet");
        created.BankName.ShouldBe("ČSOB");
        created.IBAN.ShouldBe("CZ6508000000192000014565");
        created.SWIFT.ShouldBe("GIBACZPX");
        created.CurrencyCode.ShouldBe("EUR");
        created.IsDefault.ShouldBeTrue();
    }

    /// <summary>
    /// A blank optional value means "not supplied" — storing an empty string would put an empty
    /// label on the invoice instead of leaving the field out.
    /// </summary>
    [Fact]
    public async Task AddBankAccountTool_BlankOptionalParameters_AreTreatedAsNotSupplied()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateAddAccountTool(service);

        await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["account_number"] = "555/0300",
            ["label"] = "   ",
            ["iban"] = ""
        });

        var created = CapturedAdd(service);
        created.Label.ShouldBeNull();
        created.IBAN.ShouldBeNull();
        created.IsDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task AddBankAccountTool_WithoutIssuer_Fails()
    {
        var service = StubService(issuer: null);
        var tool = CreateAddAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["account_number"] = "1/0100" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("/my-company");
        await service.DidNotReceive().AddBankAccountAsync(
            Arg.Any<long>(), Arg.Any<CreateBankAccountDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBankAccountTool_WhenSaveReportsMissingClient_Fails()
    {
        var service = StubService(BuildIssuer());
        service.AddBankAccountAsync(Arg.Any<long>(), Arg.Any<CreateBankAccountDto>(), Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var tool = CreateAddAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["account_number"] = "1/0100" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("no longer exists");
    }

    /// <summary>
    /// A second account added without asking for the default flag leaves the default where it is.
    /// The preview says so out loud: which account gets printed on the next invoice is exactly
    /// what the user is checking here, and silence about it reads as "something may have moved".
    /// </summary>
    [Fact]
    public async Task AddBankAccountTool_Preview_SecondAccountWithoutTheFlag_LeavesTheDefaultAlone()
    {
        var service = StubService(BuildIssuer(BuildAccount(id: 1, accountNumber: "111/0100")));
        var tool = CreateAddAccountTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["account_number"] = "222/0300"
        });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("current default account stays unchanged");
        preview.OutputText!.ShouldNotContain("would become the DEFAULT");
        preview.OutputText.ShouldContain("1 bank account(s)");
    }

    /// <summary>
    /// The very first account of a company becomes the default even when the model explicitly
    /// sent <c>is_default: false</c> — ClientService decides that, not the flag. The preview has
    /// to announce the outcome the database will produce, not the flag it was handed.
    /// </summary>
    [Fact]
    public async Task AddBankAccountTool_Preview_FirstAccountIsDefaultEvenWhenTheFlagSaysOtherwise()
    {
        var service = StubService(BuildIssuer());
        var tool = CreateAddAccountTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["account_number"] = "555/0300",
            ["is_default"] = "false"
        });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("DEFAULT");
        preview.OutputText.ShouldContain("the company has none yet");
    }

    /// <summary>
    /// The answer to a successful add is the stored collection with its ids, not just a
    /// confirmation. Those ids are the only handle update_bank_account and delete_bank_account
    /// have, and the model can quote only what it has been shown.
    /// </summary>
    [Fact]
    public async Task AddBankAccountTool_Execute_AnswersWithTheStoredAccountsAndTheirIds()
    {
        var beforeAdd = BuildIssuer(BuildAccount(id: 1, accountNumber: "111/0100"));
        var afterAdd = BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: true),
            BuildAccount(id: 9, accountNumber: "222/0300", label: "EUR účet",
                currencyCode: "EUR", isDefault: false));

        var service = StubService(beforeAdd);
        service.AddBankAccountAsync(Arg.Any<long>(), Arg.Any<CreateBankAccountDto>(), Arg.Any<CancellationToken>())
            .Returns(afterAdd);

        var tool = CreateAddAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["account_number"] = "222/0300",
            ["currency_code"] = "EUR",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Bank account added");
        LineFor(result.OutputText, "222/0300").ShouldContain("ID=9");
        LineFor(result.OutputText, "111/0100").ShouldContain("ID=1");   // the untouched one is listed too
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UpdateBankAccountTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void UpdateBankAccountTool_IsConfirmableAndAddressesTheAccountById()
    {
        var tool = CreateUpdateAccountTool(StubService(BuildIssuer()));

        tool.ToolName.ShouldBe("update_bank_account");
        tool.ShouldBeAssignableTo<IConfirmableChatTool>();

        var id = tool.Parameters.Single(parameter => parameter.Name == "bank_account_id");
        id.IsRequired.ShouldBeTrue();
        id.Type.ShouldBe(ChatToolParameterType.Integer);
    }

    /// <summary>
    /// The addressed account carries the new values; every other account has to be sent back
    /// exactly as it was, because the save replaces the whole collection.
    /// </summary>
    [Fact]
    public async Task UpdateBankAccountTool_ChangesOnlyTheAddressedAccount()
    {
        var service = StubService(BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", label: "CZK", isDefault: true),
            BuildAccount(id: 2, accountNumber: "222/0300", label: "EUR", currencyCode: "EUR", isDefault: false)));
        var tool = CreateUpdateAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "2",
            ["iban"] = "CZ6508000000192000014565",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var accounts = CapturedUpdate(service).BankAccount!;
        accounts.Count.ShouldBe(2);
        accounts[0].AccountNumber.ShouldBe("111/0100");
        accounts[0].IBAN.ShouldBeNull();
        accounts[0].IsDefault.ShouldBe(true);
        accounts[1].AccountNumber.ShouldBe("222/0300");
        accounts[1].Label.ShouldBe("EUR");
        accounts[1].CurrencyCode.ShouldBe("EUR");
        accounts[1].IBAN.ShouldBe("CZ6508000000192000014565");
    }

    /// <summary>
    /// Only one account may be the default one. Promoting one has to demote the other in the
    /// same save — ClientService keeps whatever flags it is given.
    /// </summary>
    [Fact]
    public async Task UpdateBankAccountTool_PromotingDefault_DemotesTheOtherAccounts()
    {
        var service = StubService(BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: true),
            BuildAccount(id: 2, accountNumber: "222/0300", isDefault: false)));
        var tool = CreateUpdateAccountTool(service);

        await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "2",
            ["is_default"] = "true"
        });

        var accounts = CapturedUpdate(service).BankAccount!;
        accounts[0].IsDefault.ShouldBe(false);
        accounts[1].IsDefault.ShouldBe(true);
    }

    /// <summary>
    /// An id the company does not have must come back as a correctable error listing the ids
    /// that exist — the model can then fix the call instead of guessing again.
    /// </summary>
    [Fact]
    public async Task UpdateBankAccountTool_UnknownId_ListsTheExistingIds()
    {
        var service = StubService(BuildIssuer(BuildAccount(id: 4)));
        var tool = CreateUpdateAccountTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "99",
            ["label"] = "X"
        });

        preview.IsSuccess.ShouldBeFalse();
        preview.ErrorMessage.ShouldContain("99");
        preview.ErrorMessage.ShouldContain("Existing IDs: 4");
    }

    /// <summary>Naming an account but no field to change on it is a refused call, not an empty save.</summary>
    [Fact]
    public async Task UpdateBankAccountTool_WithoutAnyFieldToChange_IsRefused()
    {
        var service = StubService(BuildIssuer(BuildAccount(id: 4, accountNumber: "111/0100")));
        var tool = CreateUpdateAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            // The very same values the account already has — nothing would change.
            ["bank_account_id"] = "4",
            ["account_number"] = "111/0100"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("No change was requested");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateBankAccountTool_Preview_WritesNothing()
    {
        var service = StubService(BuildIssuer(BuildAccount(id: 4, accountNumber: "111/0100")));
        var tool = CreateUpdateAccountTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "4",
            ["account_number"] = "999/0800"
        });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("111/0100");
        preview.OutputText.ShouldContain("999/0800");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A company always ends up with a default account: clearing the flag on the addressed one
    /// leaves no default at all and ClientService re-appoints the first account in the list —
    /// possibly the same one. Announcing "stops being the default" would therefore be a lie, so
    /// the call is refused before anything is read or written (review B1 on PR #299).
    /// </summary>
    [Fact]
    public async Task UpdateBankAccountTool_ClearingTheDefault_IsRefusedAndWritesNothing()
    {
        var service = StubService(BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: true),
            BuildAccount(id: 2, accountNumber: "222/0300", isDefault: false)));
        var tool = CreateUpdateAccountTool(service);

        var parameters = new Dictionary<string, string>
        {
            ["bank_account_id"] = "1",
            ["is_default"] = "false"
        };

        var preview = await tool.BuildPreviewAsync(parameters);
        preview.IsSuccess.ShouldBeFalse();
        preview.ErrorMessage.ShouldContain("is_default: true");

        parameters["confirm"] = "true";
        var result = await tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("is_default: true");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Rewriting the collection deletes and re-inserts the accounts, which the database refuses
    /// for an account that already has payment data (FK Restrict, SQLSTATE 23503). The model must
    /// get a sentence the user can act on — and the raw driver text, which names schema, table and
    /// constraint, must stay on the server: the chat reply travels to the LLM provider too.
    /// </summary>
    [Fact]
    public async Task UpdateBankAccountTool_WhenAnAccountIsPinnedByPayments_ExplainsWhyAndLogsTheDetails()
    {
        var thrown = ForeignKeyViolation();

        var service = StubService(BuildIssuer(BuildAccount(id: 4, accountNumber: "111/0100")));
        service.UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns<ClientDto?>(_ => throw thrown);

        var logger = Substitute.For<ILogger<UpdateBankAccountTool>>();
        var tool = new UpdateBankAccountTool(service, logger);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "4",
            ["account_number"] = "999/0800"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("payment data");
        result.ErrorMessage!.ShouldNotContain("foreign key constraint");
        result.ErrorMessage.ShouldNotContain("BankTransaction");

        // The exception object itself has to reach the log — it is caught here and never gets
        // as far as ChatToolExecutor, which is the only other place that would log it.
        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            thrown,
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Not every failed save is a pinned account — a too-long currency code or a dropped
    /// connection arrives as the same exception type. Blaming payment data would send the user
    /// hunting for transactions that do not exist, so anything that is not SQLSTATE 23503 gets
    /// the neutral wording.
    /// </summary>
    [Fact]
    public async Task UpdateBankAccountTool_WhenTheSaveFailsForAnotherReason_DoesNotBlameThePaymentData()
    {
        var thrown = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new PostgresException(
                "value too long for type character varying(3)",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.StringDataRightTruncation));

        var service = StubService(BuildIssuer(BuildAccount(id: 4, accountNumber: "111/0100")));
        service.UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns<ClientDto?>(_ => throw thrown);

        var logger = Substitute.For<ILogger<UpdateBankAccountTool>>();
        var tool = new UpdateBankAccountTool(service, logger);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "4",
            ["account_number"] = "999/0800"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("database rejected the change");
        result.ErrorMessage!.ShouldNotContain("payment data");
        result.ErrorMessage.ShouldNotContain("character varying");

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            thrown,
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DeleteBankAccountTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DeleteBankAccountTool_IsConfirmable()
    {
        var tool = CreateDeleteAccountTool(StubService(BuildIssuer()));

        tool.ToolName.ShouldBe("delete_bank_account");
        tool.ShouldBeAssignableTo<IConfirmableChatTool>();
        tool.Parameters.ShouldHaveSingleItem().Name.ShouldBe("bank_account_id");
    }

    /// <summary>
    /// Deleting the default account silently promotes another one. The preview says so, because
    /// the account printed on the next invoice would otherwise change without warning.
    /// </summary>
    [Fact]
    public async Task DeleteBankAccountTool_Preview_WarnsAboutLosingTheDefaultAccount()
    {
        var service = StubService(BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: true),
            BuildAccount(id: 2, accountNumber: "222/0300", isDefault: false)));
        var tool = CreateDeleteAccountTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string> { ["bank_account_id"] = "1" });

        preview.IsSuccess.ShouldBeTrue();
        preview.OutputText.ShouldContain("111/0100");
        preview.OutputText.ShouldContain("DEFAULT");
        preview.OutputText.ShouldContain("1 bank account(s) would remain");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Deleting the last account leaves invoices without payment details and without a QR code —
    /// worth saying before the user agrees, not after.
    /// </summary>
    [Fact]
    public async Task DeleteBankAccountTool_Preview_WarnsWhenItIsTheLastAccount()
    {
        var service = StubService(BuildIssuer(BuildAccount(id: 1)));
        var tool = CreateDeleteAccountTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string> { ["bank_account_id"] = "1" });

        preview.OutputText.ShouldContain("LAST bank account");
        preview.OutputText.ShouldContain("QR");
    }

    /// <summary>The deleted account disappears from the saved collection; the others survive.</summary>
    [Fact]
    public async Task DeleteBankAccountTool_RemovesOnlyTheAddressedAccount()
    {
        var service = StubService(BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: true),
            BuildAccount(id: 2, accountNumber: "222/0300", isDefault: false)));
        var tool = CreateDeleteAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "2",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var accounts = CapturedUpdate(service).BankAccount!;
        accounts.ShouldHaveSingleItem().AccountNumber.ShouldBe("111/0100");
    }

    /// <summary>
    /// The account may already be gone when the confirmed call arrives — the tool re-reads and
    /// reports the miss instead of deleting whatever holds that id now.
    /// </summary>
    [Fact]
    public async Task DeleteBankAccountTool_UnknownId_FailsWithoutWriting()
    {
        var service = StubService(BuildIssuer(BuildAccount(id: 1)));
        var tool = CreateDeleteAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["bank_account_id"] = "42" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("42");
        await service.DidNotReceive().UpdateClientAsync(
            Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteBankAccountTool_WithoutIssuer_Fails()
    {
        var tool = CreateDeleteAccountTool(StubService(issuer: null));

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["bank_account_id"] = "1" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("/my-company");
    }

    /// <summary>
    /// Deleting the default account changes something nobody asked about: the collection is
    /// re-saved without any default, so ClientService promotes the first remaining account. The
    /// answer has to name the promoted one — it is what the next invoice and its QR code carry.
    /// </summary>
    [Fact]
    public async Task DeleteBankAccountTool_DeletingTheDefault_PromotesTheFirstRemainingAndNamesIt()
    {
        var issuer = BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: true),
            BuildAccount(id: 2, accountNumber: "222/0300", isDefault: false),
            BuildAccount(id: 3, accountNumber: "333/0800", isDefault: false));

        var service = StubService(issuer);
        ReplayBankAccountSave(service, issuer);
        var tool = CreateDeleteAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "1",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        // Nothing in the saved collection claims the flag — that is what hands the decision to
        // ClientService's "first one wins" rule.
        var saved = CapturedUpdate(service).BankAccount!;
        saved.Select(account => account.AccountNumber).ShouldBe(["222/0300", "333/0800"]);
        saved.ShouldAllBe(account => account.IsDefault != true);

        LineFor(result.OutputText, "222/0300").ShouldContain("default");
        LineFor(result.OutputText, "333/0800").ShouldNotContain("default");
        result.OutputText.ShouldContain("IDs are new");
    }

    /// <summary>
    /// Deleting some other account must not move the default. The whole collection is rewritten,
    /// so the surviving default has to carry its flag along — dropped on the way, ClientService
    /// would hand the flag to whichever account happens to be first.
    /// </summary>
    [Fact]
    public async Task DeleteBankAccountTool_DeletingANonDefault_KeepsTheDefaultWhereItWas()
    {
        var issuer = BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: false),
            BuildAccount(id: 2, accountNumber: "222/0300", isDefault: false),
            BuildAccount(id: 3, accountNumber: "333/0800", isDefault: true));

        var service = StubService(issuer);
        ReplayBankAccountSave(service, issuer);
        var tool = CreateDeleteAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "2",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var saved = CapturedUpdate(service).BankAccount!;
        saved.Select(account => account.AccountNumber).ShouldBe(["111/0100", "333/0800"]);
        saved.Single(account => account.AccountNumber == "333/0800").IsDefault.ShouldBe(true);

        LineFor(result.OutputText, "333/0800").ShouldContain("default");
        LineFor(result.OutputText, "111/0100").ShouldNotContain("default");
    }

    /// <summary>
    /// The delete goes through the same guarded save as the update, so an account pinned by
    /// payment data fails the same way: an actionable sentence for the user, the driver text on
    /// the server only. A delete tool with a catch block of its own would leak that text to the
    /// LLM provider — this test is what notices.
    /// </summary>
    [Fact]
    public async Task DeleteBankAccountTool_WhenAnAccountIsPinnedByPayments_ExplainsWhyAndLogsTheDetails()
    {
        var thrown = ForeignKeyViolation();

        var service = StubService(BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: true),
            BuildAccount(id: 2, accountNumber: "222/0300", isDefault: false)));
        service.UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns<ClientDto?>(_ => throw thrown);

        var logger = Substitute.For<ILogger<DeleteBankAccountTool>>();
        var tool = new DeleteBankAccountTool(service, logger);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "2",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("payment data");
        result.ErrorMessage!.ShouldNotContain("foreign key constraint");
        result.ErrorMessage.ShouldNotContain("BankTransaction");

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            thrown,
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// The company profile can disappear between the confirmation and the save (another session,
    /// a removed tenant). The save reports it as a missing client, and the tool has to say the
    /// delete did not happen — an upbeat answer here would be a deletion the user never got.
    /// </summary>
    [Fact]
    public async Task DeleteBankAccountTool_WhenSaveReportsMissingClient_Fails()
    {
        var service = StubService(BuildIssuer(
            BuildAccount(id: 1, accountNumber: "111/0100", isDefault: true),
            BuildAccount(id: 2, accountNumber: "222/0300", isDefault: false)));
        service.UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var tool = CreateDeleteAccountTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["bank_account_id"] = "2",
            ["confirm"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("no longer exists");
    }
}
