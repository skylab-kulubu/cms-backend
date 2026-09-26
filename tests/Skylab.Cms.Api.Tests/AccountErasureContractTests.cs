using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Skylab.Cms.Application.Contracts.Services;
using Skylab.Cms.Domain.Enums;
using Skylab.Cms.Infrastructure.AccountAccess;

namespace Skylab.Cms.Api.Tests;

// Contract for PUT /internal/v1/account-erasures/{request_id}
// (.scratch/account-erasure/spec.md §2 and §3.2, ADR-0051).
[Collection(AccountErasureCollection.Name)]
public sealed class AccountErasureContractTests(AccountErasureFixture fixture) : IAsyncLifetime
{
    private const string DeletedUser = "00000000-0000-4000-8000-000000000000";
    private const string ErasureClient = "core-erasure";
    private const string EraseRole = "cms:account:erase";
    private const string Subject = "3f1c9d70-5b1e-4f7a-9c2d-8e4b6a1f0c37";
    private const string OtherPerson = "7a2e4c19-0d3b-4e8f-a1c6-5b9d2f7e3a40";
    private const string ErasureServiceAccount = "9b8d7c6e-1f2a-4b3c-8d4e-5f6a7b8c9d0e";
    private const string Editor = "c4d5e6f7-a8b9-4c0d-9e1f-2a3b4c5d6e7f";
    private const int SubjectActorColumns = 8;
    private const int SubjectDrafts = 5;

    private static readonly string[] Emails =
    [
        "ayse.yilmaz@std.yildiz.edu.tr",
        "ayse.kisisel@example.com"
    ];

    private static readonly DateTime T0 = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddDays(10).AddTicks(1_234_560);
    private static readonly DateTime T2 = T0.AddDays(20).AddTicks(6_543_210);

    private readonly HttpClient _client = fixture.Factory.CreateClient();

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Erasure_replaces_the_subject_in_every_actor_column_including_archived_rows_and_changes_nothing_else()
    {
        await SeedAsync();
        await BlockSubjectAsync(Subject);
        var itemsBefore = await SnapshotAsync("collection_items");
        var blocksBefore = await SnapshotAsync("content_blocks");
        var requestId = Guid.NewGuid();

        var response = await PutAsync(requestId, ErasureToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(requestId.ToString(), body["request_id"]!.GetValue<string>());
        Assert.Equal("completed", body["status"]!.GetValue<string>());
        var completedAt = DateTimeOffset.Parse(
            body["completed_at"]!.GetValue<string>(),
            CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, completedAt.Offset);
        Assert.InRange(completedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var counts = body["counts"]!.AsObject();
        Assert.Equal(["actor_columns_replaced", "drafts_deleted"], counts.Select(pair => pair.Key));
        Assert.Equal(SubjectActorColumns, counts["actor_columns_replaced"]!.GetValue<int>());
        Assert.Equal(0, counts["drafts_deleted"]!.GetValue<int>());

        Assert.Equal(ReplaceSubject(itemsBefore), await SnapshotAsync("collection_items"));
        Assert.Equal(ReplaceSubject(blocksBefore), await SnapshotAsync("content_blocks"));
        Assert.Equal(0, await CountSubjectColumnsAsync(Subject));
        Assert.Equal(7, await CountSubjectColumnsAsync(OtherPerson));
    }

    [Fact]
    public async Task Published_News_author_and_body_stay_as_editorial_record()
    {
        await SeedAsync();
        await BlockSubjectAsync(Subject);
        var dataBefore = await NewsDataAsync();

        var response = await PutAsync(Guid.NewGuid(), ErasureToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dataAfter = await NewsDataAsync();
        Assert.Equal(dataBefore, dataAfter);
        var active = JsonNode.Parse(dataAfter["news-edited-by-subject"])!;
        Assert.Equal("Ayşe Yılmaz", active["author"]!.GetValue<string>());
        Assert.Equal("Ayşe Yılmaz yazdı: kulüp haberi.", active["body"]!.GetValue<string>());
        var archived = JsonNode.Parse(dataAfter["news-archived-by-subject"])!;
        Assert.Equal("Ayşe Yılmaz", archived["author"]!.GetValue<string>());
    }

    [Fact]
    public async Task Erasure_deletes_the_subjects_drafts_and_leaves_everyone_elses()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        var unrelated = new[] { "draft:test", "session:" + Subject, "cd:other" };
        foreach (var key in unrelated)
            await fixture.DraftDb.StringSetAsync(key, "keep");
        var otherDraftsBefore = await DraftKeysAsync(OtherPerson);
        Assert.Equal(SubjectDrafts, (await DraftKeysAsync(Subject)).Length);

        var response = await PutAsync(Guid.NewGuid(), ErasureToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var counts = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["counts"]!;
        Assert.Equal(SubjectDrafts, counts["drafts_deleted"]!.GetValue<int>());
        Assert.Empty(await DraftKeysAsync(Subject));
        Assert.Equal(otherDraftsBefore, await DraftKeysAsync(OtherPerson));
        foreach (var key in unrelated)
            Assert.True(await fixture.DraftDb.KeyExistsAsync(key), key);
        Assert.True(await fixture.GateDb.KeyExistsAsync(AccountAccessGateContract.MarkerKey(Subject)));
    }

    [Fact]
    public async Task Repeating_the_command_returns_the_first_200_body_without_redoing_work()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        var requestId = Guid.NewGuid();

        var first = await PutAsync(requestId, ErasureToken());
        var firstBody = await first.Content.ReadAsStringAsync();
        await ExecuteAsync(
            """
            INSERT INTO collection_items ("CollectionKey", "Slug", "Data", "UpdatedBy", "CreatedAt", "UpdatedAt")
            VALUES ('News', 'written-after-receipt', '{"title":"Late"}', @subject, @t0, @t0)
            """);
        await Task.Delay(20);
        var second = await PutAsync(requestId, ErasureToken());
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
        Assert.Equal(1, await CountSubjectColumnsAsync(Subject));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM account_erasure_receipts"));
    }

    [Fact]
    public async Task Concurrent_commands_for_one_request_do_the_work_once_and_agree()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        var requestId = Guid.NewGuid();
        var token = ErasureToken();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => PutAsync(requestId, token)));
        var bodies = await Task.WhenAll(
            responses.Select(response => response.Content.ReadAsStringAsync()));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Single(bodies.Distinct());
        var counts = JsonNode.Parse(bodies[0])!["counts"]!;
        Assert.Equal(SubjectActorColumns, counts["actor_columns_replaced"]!.GetValue<int>());
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM account_erasure_receipts"));
        Assert.Equal(0, await CountSubjectColumnsAsync(Subject));
        Assert.Empty(await DraftKeysAsync(Subject));
    }

    [Fact]
    public async Task Receipt_row_holds_only_request_id_completion_time_and_counts()
    {
        await SeedAsync();
        await BlockSubjectAsync(Subject);
        var requestId = Guid.NewGuid();

        var response = await PutAsync(requestId, ErasureToken());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var columns = await RowsAsync(
            """
            SELECT column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_name = 'account_erasure_receipts'
            ORDER BY ordinal_position
            """);
        Assert.Equal(
            [
                "request_id|uuid|NO",
                "completed_at|timestamp with time zone|NO",
                "counts|jsonb|NO"
            ],
            columns.Select(row => string.Join('|', row.Values)));
        var receipt = Assert.Single(await RowsAsync(
            "SELECT request_id::text AS id, counts::text AS counts FROM account_erasure_receipts"));
        Assert.Equal(requestId.ToString(), receipt["id"]);
        Assert.True(JsonNode.DeepEquals(body["counts"], JsonNode.Parse(receipt["counts"]!)));
    }

    [Fact]
    public async Task Missing_token_is_401_and_nothing_is_erased()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);

        var response = await PutAsync(Guid.NewGuid(), token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNothingErasedAsync();
    }

    [Fact]
    public async Task Wrong_audience_is_401_and_nothing_is_erased()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        var token = fixture.CreateToken(
            ErasureClient,
            Roles("skycms", EraseRole),
            ErasureServiceAccount,
            audience: "skymail");

        var response = await PutAsync(Guid.NewGuid(), token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNothingErasedAsync();
    }

    [Theory]
    [InlineData("role-only-under-resource_access.core")]
    [InlineData("role-only-under-the-callers-own-client")]
    [InlineData("role-under-skycms-but-wrong-azp")]
    [InlineData("right-azp-without-the-erase-role")]
    [InlineData("cms-access-editor")]
    public async Task Token_without_the_erase_role_from_core_erasure_is_403_and_nothing_is_erased(
        string tokenKind)
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        var token = tokenKind switch
        {
            "role-only-under-resource_access.core" =>
                fixture.CreateToken(ErasureClient, Roles("core", EraseRole), ErasureServiceAccount),
            "role-only-under-the-callers-own-client" =>
                fixture.CreateToken(ErasureClient, Roles(ErasureClient, EraseRole), ErasureServiceAccount),
            "role-under-skycms-but-wrong-azp" =>
                fixture.CreateToken("core", Roles("skycms", EraseRole), ErasureServiceAccount),
            "right-azp-without-the-erase-role" =>
                fixture.CreateToken(ErasureClient, Roles("skycms", "cms:access"), ErasureServiceAccount),
            "cms-access-editor" => EditorToken(),
            _ => throw new ArgumentOutOfRangeException(nameof(tokenKind))
        };

        var response = await PutAsync(Guid.NewGuid(), token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemAsync(response, "erasure_forbidden");
        await AssertNothingErasedAsync();
    }

    [Fact]
    public async Task Subject_without_an_access_marker_is_409_and_nothing_is_erased()
    {
        await SeedAsync();
        await SeedDraftsAsync();

        var response = await PutAsync(Guid.NewGuid(), ErasureToken());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemAsync(response, "subject_not_blocked");
        await AssertNothingErasedAsync();
    }

    [Fact]
    public async Task Unreadable_subject_marker_is_503_and_nothing_is_erased()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await fixture.GateDb.StringSetAsync(AccountAccessGateContract.MarkerKey(Subject), "true");

        var response = await PutAsync(Guid.NewGuid(), ErasureToken());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        await AssertProblemAsync(response, "subject_block_unverifiable");
        await AssertNothingErasedAsync();
    }

    [Fact]
    public async Task Account_access_Redis_down_is_503_and_nothing_is_erased()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        await using var factory = fixture.CreateFactory(gateEndpoint: "127.0.0.1:1");
        using var client = factory.CreateClient();

        var response = await PutAsync(Guid.NewGuid(), ErasureToken(), client: client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertNothingErasedAsync();
    }

    [Fact]
    public async Task Gate_off_cannot_prove_the_block_so_it_is_503_not_409()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        await using var factory = fixture.CreateFactory(gateMode: "off");
        using var client = factory.CreateClient();

        var response = await PutAsync(Guid.NewGuid(), ErasureToken(), client: client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        await AssertProblemAsync(response, "subject_block_unverifiable");
        await AssertNothingErasedAsync();
    }

    [Fact]
    public async Task Draft_Redis_down_is_503_before_the_database_is_touched()
    {
        await SeedAsync();
        await BlockSubjectAsync(Subject);
        await using var factory = fixture.CreateFactory(
            draftRedis: "127.0.0.1:1,connectTimeout=200,abortConnect=false");
        using var client = factory.CreateClient();

        var response = await PutAsync(Guid.NewGuid(), ErasureToken(), client: client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(SubjectActorColumns, await CountSubjectColumnsAsync(Subject));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM account_erasure_receipts"));
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.7")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("X-Forwarded-Host", "cms.yildizskylab.com")]
    [InlineData("X-Real-Ip", "203.0.113.7")]
    [InlineData("Forwarded", "for=203.0.113.7;proto=https")]
    public async Task Request_through_the_public_ingress_is_404_even_with_a_valid_token(
        string header,
        string value)
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);

        var response = await PutAsync(
            Guid.NewGuid(),
            ErasureToken(),
            configure: request => request.Headers.TryAddWithoutValidation(header, value));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        await AssertNothingErasedAsync();
    }

    [Theory]
    [InlineData("request_id differs from path", """{"request_id":"{other}","subject_id":"{subject}","emails":[]}""")]
    [InlineData("unknown field", """{"request_id":"{rid}","subject_id":"{subject}","emails":[],"name":"x"}""")]
    [InlineData("duplicate field", """{"request_id":"{rid}","request_id":"{rid}","subject_id":"{subject}","emails":[]}""")]
    [InlineData("missing request_id", """{"subject_id":"{subject}","emails":[]}""")]
    [InlineData("missing subject_id", """{"request_id":"{rid}","emails":[]}""")]
    [InlineData("missing emails", """{"request_id":"{rid}","subject_id":"{subject}"}""")]
    [InlineData("null subject_id", """{"request_id":"{rid}","subject_id":null,"emails":[]}""")]
    [InlineData("subject_id not a UUID", """{"request_id":"{rid}","subject_id":"not-a-uuid","emails":[]}""")]
    [InlineData("subject_id not canonical", """{"request_id":"{rid}","subject_id":"{SUBJECT}","emails":[]}""")]
    [InlineData("four emails", """{"request_id":"{rid}","subject_id":"{subject}","emails":["a@x.tr","b@x.tr","c@x.tr","d@x.tr"]}""")]
    [InlineData("empty email", """{"request_id":"{rid}","subject_id":"{subject}","emails":[""]}""")]
    [InlineData("email over 254 chars", """{"request_id":"{rid}","subject_id":"{subject}","emails":["{long}"]}""")]
    [InlineData("emails not an array", """{"request_id":"{rid}","subject_id":"{subject}","emails":"a@x.tr"}""")]
    [InlineData("email not a string", """{"request_id":"{rid}","subject_id":"{subject}","emails":[1]}""")]
    [InlineData("body not an object", """["{rid}","{subject}"]""")]
    [InlineData("malformed JSON", """{"request_id":"{rid}","subject_id":"{subject}",""")]
    [InlineData("body over 4 KB", """{"request_id":"{rid}","subject_id":"{subject}","emails":[]{padding}}""")]
    public async Task Invalid_command_is_400_without_echoing_the_body_and_nothing_is_erased(
        string _,
        string template)
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        var requestId = Guid.NewGuid();
        var body = template
            .Replace("{rid}", requestId.ToString())
            .Replace("{other}", Guid.NewGuid().ToString())
            .Replace("{subject}", Subject)
            .Replace("{SUBJECT}", Subject.ToUpperInvariant())
            .Replace("{long}", new string('a', 245) + "@example.com")
            .Replace("{padding}", new string(' ', 4097));

        var response = await PutAsync(requestId, ErasureToken(), body: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await AssertProblemAsync(response, "invalid_erasure_command");
        Assert.DoesNotContain(Subject, problem, StringComparison.OrdinalIgnoreCase);
        await AssertNothingErasedAsync();
    }

    [Fact]
    public async Task Request_id_in_the_path_must_be_a_UUID()
    {
        await SeedAsync();
        await BlockSubjectAsync(Subject);

        var response = await PutAsync(
            Guid.NewGuid(),
            ErasureToken(),
            path: "/internal/v1/account-erasures/not-a-uuid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, "invalid_erasure_command");
        await AssertNothingErasedAsync(drafts: false);
    }

    [Fact]
    public async Task Command_must_be_sent_as_JSON()
    {
        await SeedAsync();
        await BlockSubjectAsync(Subject);
        var requestId = Guid.NewGuid();

        var response = await PutAsync(
            requestId,
            ErasureToken(),
            configure: request => request.Content = new StringContent(
                CommandBody(requestId),
                Encoding.UTF8,
                "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, "invalid_erasure_command");
        await AssertNothingErasedAsync(drafts: false);
    }

    [Fact]
    public async Task Logs_never_carry_the_subject_or_the_addresses()
    {
        await SeedAsync();
        await SeedDraftsAsync();
        await BlockSubjectAsync(Subject);
        var requestId = Guid.NewGuid();
        var unknownFieldBody =
            $$"""{"request_id":"{{requestId}}","subject_id":"{{Subject}}","emails":["{{Emails[0]}}"],"extra":"{{Emails[1]}}"}""";

        var rejected = await PutAsync(requestId, ErasureToken(), body: unknownFieldBody);
        var forbidden = await PutAsync(requestId, EditorToken());
        var notBlocked = await PutAsync(
            Guid.NewGuid(),
            ErasureToken(),
            body: null,
            subject: OtherPerson);
        var completed = await PutAsync(requestId, ErasureToken());
        var repeated = await PutAsync(requestId, ErasureToken());

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, notBlocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var logs = string.Join('\n', fixture.Logs.Entries);
        Assert.Contains(requestId.ToString(), logs);
        Assert.Contains("Executed DbCommand", logs);
        foreach (var secret in Emails.Append(Subject).Append(OtherPerson))
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cms_access_is_unchanged_for_editors_and_not_granted_to_the_erasure_client()
    {
        await SeedAsync();

        using var editorRequest = new HttpRequestMessage(HttpMethod.Get, "/cms/content?slug=/home");
        editorRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EditorToken());
        var editorResponse = await _client.SendAsync(editorRequest);
        using var erasureRequest = new HttpRequestMessage(HttpMethod.Get, "/cms/content?slug=/home");
        erasureRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ErasureToken());
        var erasureResponse = await _client.SendAsync(erasureRequest);

        Assert.Equal(HttpStatusCode.OK, editorResponse.StatusCode);
        var content = JsonNode.Parse(await editorResponse.Content.ReadAsStringAsync())!;
        Assert.Contains("SKY LAB", content.ToJsonString());
        Assert.Equal(HttpStatusCode.Forbidden, erasureResponse.StatusCode);
    }

    private string ErasureToken() =>
        fixture.CreateToken(ErasureClient, Roles("skycms", EraseRole), ErasureServiceAccount);

    private string EditorToken() =>
        fixture.CreateToken("skylab-site", Roles("skylab-site", "cms:access"), Editor);

    private static Dictionary<string, object> Roles(string client, params string[] roles) =>
        new()
        {
            [client] = new Dictionary<string, object> { ["roles"] = roles }
        };

    private static string CommandBody(Guid requestId, string subject = Subject) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["request_id"] = requestId.ToString(),
            ["subject_id"] = subject,
            ["emails"] = Emails
        });

    private async Task<HttpResponseMessage> PutAsync(
        Guid requestId,
        string? token,
        string? body = null,
        HttpClient? client = null,
        string? path = null,
        string subject = Subject,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            path ?? $"/internal/v1/account-erasures/{requestId}");
        request.Content = new StringContent(
            body ?? CommandBody(requestId, subject),
            Encoding.UTF8,
            "application/json");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        configure?.Invoke(request);
        var response = await (client ?? _client).SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static async Task<string> AssertProblemAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        var problem = JsonNode.Parse(text)!;
        Assert.Equal(code, problem["code"]!.GetValue<string>());
        Assert.Equal((int)response.StatusCode, problem["status"]!.GetValue<int>());
        return text;
    }

    private async Task AssertNothingErasedAsync(bool drafts = true)
    {
        Assert.Equal(SubjectActorColumns, await CountSubjectColumnsAsync(Subject));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM account_erasure_receipts"));
        if (drafts)
            Assert.Equal(SubjectDrafts, (await DraftKeysAsync(Subject)).Length);
    }

    private Task BlockSubjectAsync(string subject) =>
        fixture.GateDb.StringSetAsync(
            AccountAccessGateContract.MarkerKey(subject),
            AccountAccessGateContract.MarkerValue);

    // Subject S holds 4 actor columns in collection_items and 4 in
    // content_blocks, spread over active and archived rows; the other person
    // holds 7 more, and the deploy pipeline 1.
    private Task SeedAsync() => ExecuteAsync(
        """
        INSERT INTO collection_items
            ("CollectionKey", "Slug", "Data", "UpdatedBy", "Version", "CreatedAt", "UpdatedAt", "IsArchived", "ArchivedAt", "ArchivedBy")
        VALUES
            ('News', 'news-edited-by-subject',
             '{"title":"Kulüp haberi","author":"Ayşe Yılmaz","body":"Ayşe Yılmaz yazdı: kulüp haberi."}',
             @subject, 3, @t0, @t1, false, NULL, NULL),
            ('News', 'news-archived-by-subject',
             '{"title":"Eski haber","author":"Ayşe Yılmaz","body":"Eski gövde"}',
             @subject, 5, @t0, @t2, true, @t2, @subject),
            ('Teams', 'team-edited-by-other-archived-by-subject', '{"name":"Arge"}',
             @other, 2, @t0, @t1, true, @t1, @subject),
            ('News', 'news-by-other', '{"title":"Başka","author":"Mehmet","body":"Başka gövde"}',
             @other, 1, @t0, @t0, false, NULL, NULL),
            ('Teams', 'team-archived-by-other', '{"name":"Robotik"}',
             @other, 4, @t0, @t2, true, @t2, @other);

        INSERT INTO content_blocks
            ("ClientId", "Slug", "BlockPath", "BlockType", "Value", "SortOrder", "Version", "UpdatedBy", "CreatedAt", "UpdatedAt", "IsArchived", "ArchivedAt", "ArchivedBy")
        VALUES
            ('skylab-site', '/home', 'hero.title', 'Text', '"SKY LAB"', 0, 2, @subject, @t0, @t1, false, NULL, NULL),
            ('skylab-site', '/home', 'hero.subtitle', 'Text', '"Eski"', 1, 3, @subject, @t0, @t2, true, @t2, @subject),
            ('skylab-site', '/about', 'intro', 'Text', '"Hakkında"', 0, 2, @other, @t0, @t1, true, @t1, @subject),
            ('skylab-site', '/about', 'team', 'Text', '"Ekip"', 1, 1, 'deploy-pipeline', @t0, @t0, false, NULL, NULL),
            ('other-site', '/home', 'hero.title', 'Text', '"Diğer"', 0, 1, @other, @t0, @t0, true, @t0, @other);
        """);

    private async Task SeedDraftsAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var pageDrafts = scope.ServiceProvider.GetRequiredService<IDraftService>();
        var collectionDrafts = scope.ServiceProvider.GetRequiredService<ICollectionDraftService>();
        foreach (var person in new[] { Subject, OtherPerson })
        {
            await pageDrafts.SaveDraftAsync(
                "skylab-site",
                person,
                "/home",
                [new DraftBlock("hero.title", JsonValue.Create("Taslak"))]);
            await pageDrafts.SaveDraftAsync(
                "other-site",
                person,
                "/about",
                [new DraftBlock("intro", JsonValue.Create("Taslak"))]);
            await collectionDrafts.SaveItemDraftAsync(
                CollectionKey.News,
                "news-edited-by-subject",
                person,
                new JsonObject { ["title"] = "Taslak" });
            await collectionDrafts.SaveVirtualDraftAsync(
                CollectionKey.Teams,
                "arge",
                person,
                new JsonObject { ["name"] = "Taslak" });
            await collectionDrafts.SaveNewDraftAsync(
                CollectionKey.News,
                person,
                null,
                new JsonObject { ["title"] = "Yeni" });
        }
    }

    private async Task<string[]> DraftKeysAsync(string person)
    {
        var server = fixture.Redis.GetServers().Single();
        var keys = new List<string>();
        await foreach (var key in server.KeysAsync(AccountErasureFixture.DraftDatabase, "*"))
        {
            var value = key.ToString();
            if ((value.StartsWith("draft:", StringComparison.Ordinal) ||
                 value.StartsWith("cd:", StringComparison.Ordinal)) &&
                value.Contains(person, StringComparison.Ordinal))
            {
                keys.Add(value);
            }
        }

        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    private Task<long> CountSubjectColumnsAsync(string subject) => ScalarAsync<long>(
        """
        SELECT
            (SELECT count(*) FROM collection_items WHERE "UpdatedBy" = @subjectArg) +
            (SELECT count(*) FROM collection_items WHERE "ArchivedBy" = @subjectArg) +
            (SELECT count(*) FROM content_blocks WHERE "UpdatedBy" = @subjectArg) +
            (SELECT count(*) FROM content_blocks WHERE "ArchivedBy" = @subjectArg)
        """,
        ("subjectArg", subject));

    private async Task<Dictionary<string, string>> NewsDataAsync()
    {
        var rows = await RowsAsync(
            """SELECT "Slug" AS slug, "Data"::text AS data FROM collection_items WHERE "CollectionKey" = 'News'""");
        return rows.ToDictionary(row => row["slug"]!, row => row["data"]!);
    }

    private async Task<List<string>> SnapshotAsync(string table)
    {
        var rows = await RowsAsync($"""SELECT * FROM {table} ORDER BY "Slug", "Id" """);
        return rows.Select(row => JsonSerializer.Serialize(row)).ToList();
    }

    private static List<string> ReplaceSubject(IEnumerable<string> snapshot) =>
        snapshot
            .Select(json =>
            {
                var row = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)!;
                foreach (var column in new[] { "UpdatedBy", "ArchivedBy" })
                {
                    if (row[column] == Subject)
                        row[column] = DeletedUser;
                }

                return JsonSerializer.Serialize(row);
            })
            .ToList();

    private async Task<List<Dictionary<string, string?>>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, string?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, string?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i)
                    ? null
                    : reader.GetValue(i) switch
                    {
                        DateTime value => value.ToString("O", CultureInfo.InvariantCulture),
                        var value => Convert.ToString(value, CultureInfo.InvariantCulture)
                    };
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("subject", Subject);
        command.Parameters.AddWithValue("other", OtherPerson);
        command.Parameters.AddWithValue("t0", T0);
        command.Parameters.AddWithValue("t1", T1);
        command.Parameters.AddWithValue("t2", T2);
        await command.ExecuteNonQueryAsync();
    }
}
