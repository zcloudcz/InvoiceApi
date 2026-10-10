using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <summary>
    /// One database table for ALL documents: received (supplier) invoices move from the parallel
    /// <c>ReceivedInvoice</c> / <c>ReceivedInvoiceItem</c> tables into <c>Invoice</c> / <c>InvoiceItem</c>
    /// with <c>DocumentType = 5</c> (ReceivedInvoice).
    ///
    /// Hand-written data move (the EF scaffold would simply DROP the received tables = data loss).
    /// The schema operations are the EF ones (index filter, <c>ReceivedDate</c>, drop of the old
    /// <c>ReceivedInvoiceId</c> columns); the data is moved by <see cref="UnifyDocumentsInInvoiceTableSql"/>.
    ///
    /// Safety net: the whole migration (all statements + the __EFMigrationsHistory row) runs in ONE transaction,
    /// because EF Core wraps every migration in a transaction unless an operation explicitly opts out (none here).
    /// The data move ends with a self-check that compares the legacy tables with the new rows and RAISEs an
    /// exception on any difference, which rolls the tenant's migration back untouched.
    ///
    /// Table names are unqualified on purpose: they resolve to the tenant schema through search_path.
    ///
    /// Old tables are NOT dropped: they are renamed to <c>*_legacy</c> (FKs removed, so they cannot block deletes)
    /// together with the persistent map <c>ReceivedInvoiceIdMap_legacy("OldId","NewId")</c>. A later release drops
    /// them (see DEVGUIDE "One table for all documents").
    /// </summary>
    public partial class UnifyDocumentsInInvoiceTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. The unique document-number index must not apply to received documents (supplier numbers repeat
            //    across suppliers and can equal our own). Recreated BEFORE any received row is inserted.
            migrationBuilder.DropIndex(
                name: "IX_Invoice_DocumentNumber",
                table: "Invoice");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedDate",
                table: "Invoice",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_DocumentNumber",
                table: "Invoice",
                column: "DocumentNumber",
                unique: true,
                filter: "\"DocumentNumber\" IS NOT NULL AND \"DocumentNumber\" <> 'DRAFT' AND \"Status\" <> 5 AND \"DocumentType\" <> 5");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_ReceivedDate",
                table: "Invoice",
                column: "ReceivedDate");

            // 2. Id map + data move + self-check (one plpgsql block; any RAISE EXCEPTION rolls everything back).
            migrationBuilder.Sql(UnifyDocumentsInInvoiceTableSql.CreateIdMap);
            migrationBuilder.Sql(UnifyDocumentsInInvoiceTableSql.MoveDataAndSelfCheck);

            // 3. Structural clean-up: the old pointers are no longer needed (data is already in InvoiceId).
            migrationBuilder.DropForeignKey(
                name: "FK_InboundInvoiceEmail_ReceivedInvoice_ReceivedInvoiceId",
                table: "InboundInvoiceEmail");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentMatch_ReceivedInvoice_ReceivedInvoiceId",
                table: "PaymentMatch");

            migrationBuilder.DropIndex(
                name: "IX_PaymentMatch_ReceivedInvoiceId",
                table: "PaymentMatch");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentMatch_Target",
                table: "PaymentMatch");

            migrationBuilder.DropIndex(
                name: "IX_InboundInvoiceEmail_ReceivedInvoiceId",
                table: "InboundInvoiceEmail");

            migrationBuilder.DropColumn(
                name: "ReceivedInvoiceId",
                table: "PaymentMatch");

            migrationBuilder.DropColumn(
                name: "ReceivedInvoiceId",
                table: "InboundInvoiceEmail");

            // No defaultValue on purpose (the scaffold would write 0 into NULL rows and trip the FK):
            // every row got its InvoiceId in step 2, a leftover NULL must fail the migration loudly.
            migrationBuilder.AlterColumn<long>(
                name: "InvoiceId",
                table: "PaymentMatch",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            // 4. Park the old tables (kept for audit, dropped by a later release).
            migrationBuilder.Sql(UnifyDocumentsInInvoiceTableSql.ParkLegacyTables);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not supported by design: ids of received documents changed and the old tables were parked, so an
            // automatic revert could not be exact. Restore the tenant from a backup instead (the *_legacy tables
            // and ReceivedInvoiceIdMap_legacy stay in the schema for audit).
            throw new NotSupportedException(
                "UnifyDocumentsInInvoiceTable cannot be reverted. Restore the database from a backup.");
        }
    }

    /// <summary>
    /// The raw SQL of <see cref="UnifyDocumentsInInvoiceTable"/>, kept apart so it is readable and testable.
    /// Legacy status -> Invoice status: Received(1)->Draft(1), Approved(2)->Completed(2), Paid(3)->Paid(3),
    /// Deleted(5)->Deleted(5), Rejected(4)->Deleted(5). The number 4 is NEVER copied: for issued documents it means
    /// Creditnoted. Approved rows with a recorded payment become PartiallyPaid(6).
    /// </summary>
    internal static class UnifyDocumentsInInvoiceTableSql
    {
        /// <summary>Persistent old-id -> new-id map (also used by the move itself).</summary>
        public const string CreateIdMap = """
            CREATE TABLE "ReceivedInvoiceIdMap_legacy" (
                "OldId" bigint NOT NULL,
                "NewId" bigint NOT NULL,
                CONSTRAINT "PK_ReceivedInvoiceIdMap_legacy" PRIMARY KEY ("OldId"),
                CONSTRAINT "UQ_ReceivedInvoiceIdMap_legacy_NewId" UNIQUE ("NewId")
            )
            """;

        public const string MoveDataAndSelfCheck = """
            DO $unify$
            DECLARE
                v_issuer     bigint;
                v_seq        text;
                v_base       bigint;
                v_rows       bigint;
                v_n          bigint;
                v_num        numeric;
                b_docs       bigint;
                b_total      numeric;
                b_items      bigint;
                b_items_sum  numeric;
                b_pm         bigint;
                b_inbound    bigint;
                b_files      bigint;
                b_notif      bigint;
                b_alerts     bigint;
                b_received   bigint;
                b_approved   bigint;
                b_paid       bigint;
                b_deleted    bigint;
            BEGIN
                -- ===== BEFORE snapshot (from the legacy tables, which stay untouched) =====
                SELECT COUNT(*), COALESCE(SUM("TotalWithVat"), 0),
                       COUNT(*) FILTER (WHERE "Status" = 1), COUNT(*) FILTER (WHERE "Status" = 2),
                       COUNT(*) FILTER (WHERE "Status" = 3), COUNT(*) FILTER (WHERE "Status" IN (4, 5))
                  INTO b_docs, b_total, b_received, b_approved, b_paid, b_deleted
                  FROM "ReceivedInvoice";
                SELECT COUNT(*), COALESCE(SUM("TotalWithVat"), 0) INTO b_items, b_items_sum FROM "ReceivedInvoiceItem";
                SELECT COUNT(*) INTO b_pm      FROM "PaymentMatch"        WHERE "ReceivedInvoiceId" IS NOT NULL;
                SELECT COUNT(*) INTO b_inbound FROM "InboundInvoiceEmail" WHERE "ReceivedInvoiceId" IS NOT NULL;
                SELECT COUNT(*) INTO b_files   FROM "FileAttachment"      WHERE "EntityName" = 'ReceivedInvoice';
                SELECT COUNT(*) INTO b_notif   FROM "Notification"        WHERE "RelatedEntityType" = 'ReceivedInvoice';
                SELECT COUNT(*) INTO b_alerts  FROM "Alert"               WHERE "RelatedEntityType" = 'ReceivedInvoice';

                -- Rows that cannot be represented with a single InvoiceId: stop instead of silently losing a link.
                IF EXISTS (SELECT 1 FROM "PaymentMatch" WHERE "ReceivedInvoiceId" IS NOT NULL AND "InvoiceId" IS NOT NULL) THEN
                    RAISE EXCEPTION 'UnifyDocuments: PaymentMatch rows point to both an issued and a received invoice';
                END IF;
                IF EXISTS (SELECT 1 FROM "InboundInvoiceEmail" WHERE "ReceivedInvoiceId" IS NOT NULL AND "InvoiceId" IS NOT NULL) THEN
                    RAISE EXCEPTION 'UnifyDocuments: InboundInvoiceEmail rows link both an issued and a received invoice';
                END IF;

                -- ===== MOVE (only when there is something to move: empty and brand-new tenants skip it) =====
                IF b_docs > 0 THEN
                    -- IssuerId is NOT NULL on Invoice: for a received document it is OUR company (lowest IsIssuer client).
                    SELECT MIN("Id") INTO v_issuer FROM "Client" WHERE "IsIssuer";
                    IF v_issuer IS NULL THEN
                        RAISE EXCEPTION 'UnifyDocuments: the tenant has no issuer client (Client.IsIssuer = true)';
                    END IF;

                    -- Deterministic ids: reserve ONE contiguous block from the Invoice identity sequence and give the
                    -- received rows the ids base, base+1, ... in order of their old Id (never below the current max Id).
                    v_seq  := pg_get_serial_sequence('"Invoice"', 'Id');
                    v_base := GREATEST(nextval(v_seq), (SELECT COALESCE(MAX("Id"), 0) FROM "Invoice") + 1);
                    PERFORM setval(v_seq, v_base + b_docs - 1);

                    INSERT INTO "ReceivedInvoiceIdMap_legacy" ("OldId", "NewId")
                    SELECT "Id", v_base + ROW_NUMBER() OVER (ORDER BY "Id") - 1 FROM "ReceivedInvoice";

                    INSERT INTO "Invoice" (
                        "Id", "DocumentType", "Status", "DocumentNumber", "IssueDate", "DueDate", "TaxableSupplyDate",
                        "ClientId", "IssuerId", "VariableSymbol", "BankAccountNumber", "IBAN", "SWIFT", "PaymentMethod",
                        "TotalBeforeVat", "TotalVat", "TotalWithVat", "CurrencyId", "Notes",
                        "IsExported", "IsSentByEmail", "PaidAt", "PaidAmount", "ExchangeRate", "ExchangeRateDate",
                        "ReceivedDate", "InvoiceType", "CreatedAt", "UpdatedAt", "CreatedByUserId", "UpdatedByUserId")
                    SELECT
                        m."NewId", 5,
                        CASE r."Status"
                            WHEN 1 THEN 1
                            WHEN 2 THEN CASE WHEN p.paid > 0 THEN 6 ELSE 2 END
                            WHEN 3 THEN 3
                            WHEN 4 THEN 5   -- Rejected is abolished -> Deleted (never copy 4: it means Creditnoted here)
                            WHEN 5 THEN 5
                        END,
                        r."DocumentNumber", r."IssueDate", r."DueDate", r."TaxableSupplyDate",
                        r."SupplierId", v_issuer, r."VariableSymbol", r."BankAccountNumber", r."IBAN", r."SWIFT", r."PaymentMethod",
                        r."TotalBeforeVat", r."TotalVat", r."TotalWithVat", r."CurrencyId", r."Notes",
                        FALSE, FALSE, r."PaidAt",
                        CASE WHEN r."Status" = 3 THEN r."TotalWithVat" ELSE p.paid END,
                        r."ExchangeRate", r."ExchangeRateDate",
                        r."ReceivedDate", 'Invoice', r."CreatedAt", r."UpdatedAt", r."CreatedByUserId", r."UpdatedByUserId"
                    FROM "ReceivedInvoice" r
                    JOIN "ReceivedInvoiceIdMap_legacy" m ON m."OldId" = r."Id"
                    CROSS JOIN LATERAL (
                        SELECT COALESCE(SUM(pm."MatchedAmount"), 0) AS paid
                          FROM "PaymentMatch" pm WHERE pm."ReceivedInvoiceId" = r."Id") p
                    ORDER BY r."Id";

                    INSERT INTO "InvoiceItem" (
                        "InvoiceId", "OrderIndex", "Description", "Quantity", "Unit", "UnitPrice", "VatRateId",
                        "VatRatePercentage", "TotalBeforeVat", "VatAmount", "TotalWithVat", "IsTextRow", "VatRegime",
                        "ReverseChargeCodeId", "InformationalVatAmount", "ProductCode", "Notes",
                        "CreatedAt", "UpdatedAt", "CreatedByUserId", "UpdatedByUserId")
                    SELECT
                        m."NewId", i."OrderIndex", i."Description", i."Quantity", i."Unit", i."UnitPrice", i."VatRateId",
                        i."VatRatePercentage", i."TotalBeforeVat", i."VatAmount", i."TotalWithVat", FALSE, i."VatRegime",
                        i."ReverseChargeCodeId", i."InformationalVatAmount", i."ProductCode", i."Notes",
                        i."CreatedAt", i."UpdatedAt", i."CreatedByUserId", i."UpdatedByUserId"
                    FROM "ReceivedInvoiceItem" i
                    JOIN "ReceivedInvoiceIdMap_legacy" m ON m."OldId" = i."ReceivedInvoiceId"
                    ORDER BY i."Id";

                    -- Pointers to received invoices: ONLY the id changes (set-based, all rows read the OLD value).
                    UPDATE "PaymentMatch" x SET "InvoiceId" = m."NewId"
                      FROM "ReceivedInvoiceIdMap_legacy" m WHERE x."ReceivedInvoiceId" = m."OldId";
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    IF v_rows <> b_pm THEN RAISE EXCEPTION 'UnifyDocuments: PaymentMatch remapped % of % rows', v_rows, b_pm; END IF;

                    UPDATE "InboundInvoiceEmail" x SET "InvoiceId" = m."NewId"
                      FROM "ReceivedInvoiceIdMap_legacy" m WHERE x."ReceivedInvoiceId" = m."OldId";
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    IF v_rows <> b_inbound THEN RAISE EXCEPTION 'UnifyDocuments: InboundInvoiceEmail remapped % of % rows', v_rows, b_inbound; END IF;

                    -- Polymorphic pointers keep the type string 'ReceivedInvoice' (the UI still routes by it).
                    UPDATE "FileAttachment" x SET "RecordId" = m."NewId"
                      FROM "ReceivedInvoiceIdMap_legacy" m WHERE x."EntityName" = 'ReceivedInvoice' AND x."RecordId" = m."OldId";
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    IF v_rows <> b_files THEN RAISE EXCEPTION 'UnifyDocuments: FileAttachment remapped % of % rows', v_rows, b_files; END IF;

                    UPDATE "Notification" x SET "RelatedEntityId" = m."NewId"
                      FROM "ReceivedInvoiceIdMap_legacy" m WHERE x."RelatedEntityType" = 'ReceivedInvoice' AND x."RelatedEntityId" = m."OldId";
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    IF v_rows <> b_notif THEN RAISE EXCEPTION 'UnifyDocuments: Notification remapped % of % rows', v_rows, b_notif; END IF;

                    UPDATE "Alert" x SET "RelatedEntityId" = m."NewId"
                      FROM "ReceivedInvoiceIdMap_legacy" m WHERE x."RelatedEntityType" = 'ReceivedInvoice' AND x."RelatedEntityId" = m."OldId";
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    IF v_rows <> b_alerts THEN RAISE EXCEPTION 'UnifyDocuments: Alert remapped % of % rows', v_rows, b_alerts; END IF;
                END IF;

                -- ===== SELF-CHECK: before (legacy tables) vs after (Invoice / InvoiceItem), same transaction =====
                SELECT COUNT(*), COALESCE(SUM("TotalWithVat"), 0) INTO v_n, v_num FROM "Invoice" WHERE "DocumentType" = 5;
                IF v_n <> b_docs OR v_num <> b_total THEN
                    RAISE EXCEPTION 'UnifyDocuments: documents % / total % after, % / % before', v_n, v_num, b_docs, b_total;
                END IF;

                SELECT COUNT(*) INTO v_n FROM "ReceivedInvoiceIdMap_legacy";
                IF v_n <> b_docs THEN RAISE EXCEPTION 'UnifyDocuments: id map has % rows, expected %', v_n, b_docs; END IF;

                SELECT COUNT(*), COALESCE(SUM(it."TotalWithVat"), 0) INTO v_n, v_num
                  FROM "InvoiceItem" it JOIN "Invoice" inv ON inv."Id" = it."InvoiceId" WHERE inv."DocumentType" = 5;
                IF v_n <> b_items OR v_num <> b_items_sum THEN
                    RAISE EXCEPTION 'UnifyDocuments: items % / total % after, % / % before', v_n, v_num, b_items, b_items_sum;
                END IF;

                -- status mapping (4 is never copied): Received=Draft, Approved=Completed+PartiallyPaid, Paid=Paid, Rejected+Deleted=Deleted
                SELECT COUNT(*) INTO v_n FROM "Invoice" WHERE "DocumentType" = 5 AND "Status" NOT IN (1, 2, 3, 5, 6);
                IF v_n <> 0 THEN RAISE EXCEPTION 'UnifyDocuments: % received documents have an invalid status', v_n; END IF;
                SELECT COUNT(*) INTO v_n FROM "Invoice" WHERE "DocumentType" = 5 AND "Status" = 1;
                IF v_n <> b_received THEN RAISE EXCEPTION 'UnifyDocuments: Draft % <> Received %', v_n, b_received; END IF;
                SELECT COUNT(*) INTO v_n FROM "Invoice" WHERE "DocumentType" = 5 AND "Status" IN (2, 6);
                IF v_n <> b_approved THEN RAISE EXCEPTION 'UnifyDocuments: Completed+PartiallyPaid % <> Approved %', v_n, b_approved; END IF;
                SELECT COUNT(*) INTO v_n FROM "Invoice" WHERE "DocumentType" = 5 AND "Status" = 3;
                IF v_n <> b_paid THEN RAISE EXCEPTION 'UnifyDocuments: Paid % <> Paid %', v_n, b_paid; END IF;
                SELECT COUNT(*) INTO v_n FROM "Invoice" WHERE "DocumentType" = 5 AND "Status" = 5;
                IF v_n <> b_deleted THEN RAISE EXCEPTION 'UnifyDocuments: Deleted % <> Rejected+Deleted %', v_n, b_deleted; END IF;

                -- pointers: same number of rows now point to received documents, and none points to an unmapped id
                SELECT COUNT(*) INTO v_n FROM "PaymentMatch" pm JOIN "Invoice" inv ON inv."Id" = pm."InvoiceId" WHERE inv."DocumentType" = 5;
                IF v_n <> b_pm THEN RAISE EXCEPTION 'UnifyDocuments: % payment matches point to received documents, expected %', v_n, b_pm; END IF;
                SELECT COUNT(*) INTO v_n FROM "InboundInvoiceEmail" e JOIN "Invoice" inv ON inv."Id" = e."InvoiceId" WHERE inv."DocumentType" = 5;
                IF v_n <> b_inbound THEN RAISE EXCEPTION 'UnifyDocuments: % inbound e-mails link received documents, expected %', v_n, b_inbound; END IF;

                SELECT COUNT(*) INTO v_n FROM "FileAttachment" f
                 WHERE f."EntityName" = 'ReceivedInvoice'
                   AND NOT EXISTS (SELECT 1 FROM "Invoice" inv WHERE inv."Id" = f."RecordId" AND inv."DocumentType" = 5);
                IF v_n <> 0 THEN RAISE EXCEPTION 'UnifyDocuments: % file attachments point to an unmapped id', v_n; END IF;
                SELECT COUNT(*) INTO v_n FROM "Notification" f
                 WHERE f."RelatedEntityType" = 'ReceivedInvoice'
                   AND NOT EXISTS (SELECT 1 FROM "Invoice" inv WHERE inv."Id" = f."RelatedEntityId" AND inv."DocumentType" = 5);
                IF v_n <> 0 THEN RAISE EXCEPTION 'UnifyDocuments: % notifications point to an unmapped id', v_n; END IF;
                SELECT COUNT(*) INTO v_n FROM "Alert" f
                 WHERE f."RelatedEntityType" = 'ReceivedInvoice'
                   AND NOT EXISTS (SELECT 1 FROM "Invoice" inv WHERE inv."Id" = f."RelatedEntityId" AND inv."DocumentType" = 5);
                IF v_n <> 0 THEN RAISE EXCEPTION 'UnifyDocuments: % alerts point to an unmapped id', v_n; END IF;
            END
            $unify$
            """;

        /// <summary>Removes the FKs of the old tables (so they cannot block deletes elsewhere) and renames them.</summary>
        public const string ParkLegacyTables = """
            DO $legacy$
            DECLARE c record;
            BEGIN
                FOR c IN
                    SELECT con.conrelid::regclass::text AS tbl, con.conname
                      FROM pg_constraint con
                     WHERE con.contype = 'f'
                       AND con.conrelid IN ('"ReceivedInvoice"'::regclass, '"ReceivedInvoiceItem"'::regclass)
                LOOP
                    EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', c.tbl, c.conname);
                END LOOP;
            END
            $legacy$;
            ALTER TABLE "ReceivedInvoiceItem" RENAME TO "ReceivedInvoiceItem_legacy";
            ALTER TABLE "ReceivedInvoice" RENAME TO "ReceivedInvoice_legacy";
            """;
    }
}
