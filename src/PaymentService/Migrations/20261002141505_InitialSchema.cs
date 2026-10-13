using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
                suppressTransaction: true);

            migrationBuilder.CreateTable(
                name: "payment_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    order_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    phone = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false, collation: "Latin1_General_100_BIN2"),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    commission = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    processing = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true, collation: "Latin1_General_100_BIN2"),
                    fail_code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ip = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    user_agent = table.Column<string>(type: "nvarchar(max)", nullable: true, collation: "Latin1_General_100_BIN2"),
                    pan_masked = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    pan_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    card_holder = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true, collation: "Latin1_General_100_BIN2"),
                    otp_id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    otp_sent_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    otp_attempts = table.Column<int>(type: "int", nullable: false),
                    otp_resends = table.Column<int>(type: "int", nullable: false),
                    card_attempts = table.Column<int>(type: "int", nullable: false),
                    sv_card_token = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    last_activity_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_sessions", x => x.id);
                    table.CheckConstraint("ck_payment_sessions_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_payment_sessions_commission_non_negative", "commission >= 0");
                    table.CheckConstraint("ck_payment_sessions_total_consistent", "total = amount + commission");
                    table.CheckConstraint("payment_session_status", "status IN ('CREATED', 'OTP_SENDING', 'OTP_SENT', 'OTP_VERIFYING', 'CARD_VERIFIED', 'HELD', 'CONFIRMING', 'CANCELLING', 'CANCELLED', 'RETURNING', 'RETURNED', 'PAYING', 'PENDING_CHECK', 'PAID', 'CREDITING', 'REVERSING', 'REVERSED', 'SUCCESS', 'NEEDS_MANUAL', 'FAILED', 'EXPIRED')");
                });

            migrationBuilder.CreateTable(
                name: "humo_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    system = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false, collation: "Latin1_General_100_BIN2"),
                    method = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    request_masked = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    response_masked = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    duration_ms = table.Column<int>(type: "int", nullable: false),
                    error_code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_humo_log", x => x.id);
                    table.CheckConstraint("ck_humo_log_request_masked_json", "ISJSON(request_masked) = 1");
                    table.CheckConstraint("ck_humo_log_response_masked_json", "ISJSON(response_masked) = 1");
                    table.ForeignKey(
                        name: "fk_humo_log_session_id",
                        column: x => x.session_id,
                        principalTable: "payment_sessions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    commission = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false, collation: "Latin1_General_100_BIN2"),
                    sv_ref_num = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    sv_stan = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    sv_date12 = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    sv_resp = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true, collation: "Latin1_General_100_BIN2"),
                    sv_status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true, collation: "Latin1_General_100_BIN2"),
                    humo_payment_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    humo_rrn = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    humo_appr_code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    humo_action = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true, collation: "Latin1_General_100_BIN2"),
                    amount_cardholder = table.Column<long>(type: "bigint", nullable: true),
                    operator_tx_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, collation: "Latin1_General_100_BIN2"),
                    check_attempts = table.Column<int>(type: "int", nullable: false),
                    next_check_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("payment_status", "status IN ('PAYING', 'HELD', 'CONFIRMING', 'CANCELLING', 'CANCELLED', 'RETURNING', 'RETURNED', 'PENDING_CHECK', 'PAID', 'CREDITING', 'SUCCESS', 'FAILED', 'REVERSING', 'REVERSED', 'NEEDS_MANUAL')");
                    table.ForeignKey(
                        name: "fk_payments_session_id",
                        column: x => x.session_id,
                        principalTable: "payment_sessions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "svgate_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    method = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    request_masked = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    response_masked = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    duration_ms = table.Column<int>(type: "int", nullable: false),
                    error_code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_svgate_log", x => x.id);
                    table.CheckConstraint("ck_svgate_log_request_masked_json", "ISJSON(request_masked) = 1");
                    table.CheckConstraint("ck_svgate_log_response_masked_json", "ISJSON(response_masked) = 1 OR ISJSON('[' + response_masked + ']') = 1");
                    table.ForeignKey(
                        name: "fk_svgate_log_session_id",
                        column: x => x.session_id,
                        principalTable: "payment_sessions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    payment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    number = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    data = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipts", x => x.id);
                    table.CheckConstraint("ck_receipts_data_json", "ISJSON(data) = 1");
                    table.ForeignKey(
                        name: "fk_receipts_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_humo_log_session_id",
                table: "humo_log",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_sessions_pan_hash",
                table: "payment_sessions",
                column: "pan_hash");

            migrationBuilder.CreateIndex(
                name: "uq_payment_sessions_order_id",
                table: "payment_sessions",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payment_sessions_session_token_hash",
                table: "payment_sessions",
                column: "session_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_payments_session_id",
                table: "payments",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_receipts_number",
                table: "receipts",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_receipts_payment_id",
                table: "receipts",
                column: "payment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_svgate_log_session_id",
                table: "svgate_log",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "humo_log");

            migrationBuilder.DropTable(
                name: "receipts");

            migrationBuilder.DropTable(
                name: "svgate_log");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "payment_sessions");
        }
    }
}
