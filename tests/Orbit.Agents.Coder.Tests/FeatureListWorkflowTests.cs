using System.Text;
using Orbit.Agents.Coder.Coder;
using Orbit.Agents.Coder.Coder.Workflow;
using Xunit;

namespace Orbit.Agents.Coder.Tests;

public class FeatureListWorkflowTests
{
    [Fact]
    public void FeatureStore_roundtrips_and_picks_next_respecting_deps()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-coder-fl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CoderFeatureStore(root);
            var doc = new CoderFeatureListDocument
            {
                Goal = "demo",
                SpecMarkdown = "# Spec",
                Features =
                [
                    new CoderFeatureItem
                    {
                        Id = "f1",
                        Title = "A",
                        Status = CoderFeatureStatus.Done
                    },
                    new CoderFeatureItem
                    {
                        Id = "f2",
                        Title = "B",
                        Status = CoderFeatureStatus.Pending,
                        DependsOn = ["f1"]
                    },
                    new CoderFeatureItem
                    {
                        Id = "f3",
                        Title = "C",
                        Status = CoderFeatureStatus.Pending,
                        DependsOn = ["f9"]
                    }
                ]
            };
            store.Save(doc);

            var loaded = store.TryLoad();
            Assert.NotNull(loaded);
            Assert.Equal("demo", loaded!.Goal);
            Assert.True(File.Exists(store.SpecPath));

            var next = CoderFeatureStore.PickNext(loaded);
            Assert.NotNull(next);
            Assert.Equal("f2", next!.Id);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void TryParseJsonObject_reads_fenced_json()
    {
        const string text = """
            Here is the plan
            ```json
            {"features":[{"id":"f1","title":"x"}]}
            ```
            """;
        Assert.True(CoderFeatureStore.TryParseJsonObject(text, out var json));
        Assert.Contains("\"f1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRoot_is_under_data_feature_lists_agentId()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-fl-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var path = CoderFeatureStore.ResolveRoot("coder", contentRoot);
            var norm = path.Replace('\\', '/');
            Assert.Contains("/data/feature-lists/coder", norm, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void FromPlanner_maps_verify_onto_feature()
    {
        var doc = CoderFeatureStore.FromPlanner(
            "build app",
            "use SQLite",
            "full planner text",
            new CoderPlannerPayload
            {
                SpecMarkdown = "# Product",
                Stack = ["React"],
                DesignPrinciples = ["minimal"],
                Features =
                [
                    new CoderPlannerFeature
                    {
                        Id = "auth",
                        Title = "Login",
                        Description = "sign in",
                        Acceptance = ["can log in"],
                        Verify = ["dotnet test --filter Auth"]
                    }
                ]
            });

        Assert.Equal("build app", doc.Goal);
        Assert.Equal("use SQLite", doc.Constraints);
        Assert.Single(doc.Features);
        Assert.Equal(CoderFeatureStatus.Pending, doc.Features[0].Status);
        Assert.Equal("auth", doc.Features[0].Id);
        Assert.Contains("dotnet test --filter Auth", doc.Features[0].Verify);
        Assert.Contains("dotnet test --filter Auth", doc.Features[0].VerificationPlan);
    }

    [Fact]
    public void Summarize_includes_lastFindings_and_verify()
    {
        var doc = new CoderFeatureListDocument
        {
            Goal = "demo",
            Features =
            [
                new CoderFeatureItem
                {
                    Id = "f1",
                    Title = "Login",
                    Status = CoderFeatureStatus.Failed,
                    Verify = ["dotnet test --filter Auth"],
                    VerificationPlan = CoderFeatureStore.FormatVerifyPlan(["dotnet test --filter Auth"]),
                    LastFindings = "test failed: Assert.Equal expected 200"
                }
            ]
        };

        var text = CoderFeatureStore.Summarize(doc);
        Assert.Contains("lastFindings:", text, StringComparison.Ordinal);
        Assert.Contains("test failed", text, StringComparison.Ordinal);
        Assert.Contains("dotnet test --filter Auth", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PickNext_skips_in_progress_and_ready()
    {
        var doc = new CoderFeatureListDocument
        {
            Goal = "demo",
            Features =
            [
                new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.InProgress },
                new CoderFeatureItem { Id = "f2", Title = "B", Status = CoderFeatureStatus.Ready },
                new CoderFeatureItem { Id = "f3", Title = "C", Status = CoderFeatureStatus.Pending }
            ]
        };

        Assert.Equal("f3", CoderFeatureStore.PickNext(doc)!.Id);
    }

    [Fact]
    public void TryLoad_quarantines_corrupt_json_instead_of_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-coder-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CoderFeatureStore(root);
            File.WriteAllText(store.FeatureListPath, "{ \"goal\": \"half-writ");

            Assert.Null(store.TryLoad());
            Assert.False(File.Exists(store.FeatureListPath));
            Assert.NotEmpty(Directory.GetFiles(root, CoderFeatureStore.FeatureListFileName + ".corrupt-*"));

            store.Save(new CoderFeatureListDocument { Goal = "fresh" });
            Assert.Equal("fresh", store.TryLoad()!.Goal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void PickNext_skips_features_attempted_in_the_same_run()
    {
        var doc = new CoderFeatureListDocument
        {
            Goal = "demo",
            Features =
            [
                new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.Failed },
                new CoderFeatureItem { Id = "f2", Title = "B", Status = CoderFeatureStatus.Pending }
            ]
        };

        Assert.Equal("f1", CoderFeatureStore.PickNext(doc)!.Id);

        var attempted = new HashSet<string>(["f1"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("f2", CoderFeatureStore.PickNext(doc, attempted)!.Id);
    }

    [Fact]
    public void SetFeatureStatus_done_requires_notes_and_sets_VerifiedAt()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-done-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-done-test", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-done-test", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "g",
                Features = [new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.Ready }]
            });

            var rejected = workflow.SetFeatureStatus("f1", "done", notes: null);
            Assert.Contains("requires notes", rejected, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CoderFeatureStatus.Ready, store.TryLoad()!.Features[0].Status);

            var noVerdict = workflow.SetFeatureStatus("f1", "done", notes: "dotnet test — passed");
            Assert.Contains("VERDICT: PASS", noVerdict, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CoderFeatureStatus.Ready, store.TryLoad()!.Features[0].Status);

            var ok = workflow.SetFeatureStatus(
                "f1", "done", notes: "VERDICT: PASS\ndotnet test — passed");
            Assert.Contains("Updated `f1`", ok, StringComparison.Ordinal);
            Assert.Contains("done=", ok, StringComparison.Ordinal);
            var loaded = store.TryLoad()!.Features[0];
            Assert.Equal(CoderFeatureStatus.Done, loaded.Status);
            Assert.NotNull(loaded.VerifiedAt);
            Assert.Contains("dotnet test", loaded.LastFindings, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ClaimReadyForCodingTurn_marks_ready_in_progress_and_builds_brief()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-claim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-claim-test", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-claim-test", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "ship",
                Features =
                [
                    new CoderFeatureItem
                    {
                        Id = "f1",
                        Title = "Login",
                        Status = CoderFeatureStatus.Ready,
                        Acceptance = ["user can log in"],
                        Verify = ["dotnet test"]
                    },
                    new CoderFeatureItem
                    {
                        Id = "f2",
                        Title = "Later",
                        Status = CoderFeatureStatus.Pending
                    }
                ]
            });

            var claim = workflow.ClaimReadyForCodingTurn();
            Assert.True(claim.Ok);
            Assert.Equal(["f1"], claim.ClaimedIds);
            Assert.Contains("Login", claim.Message, StringComparison.Ordinal);
            Assert.Contains("dotnet test", claim.Message, StringComparison.Ordinal);
            var loaded = store.TryLoad()!;
            Assert.Equal(CoderFeatureStatus.InProgress, loaded.Features[0].Status);
            Assert.Equal(1, loaded.Features[0].CodingTurnCount);
            Assert.Equal(CoderFeatureStatus.Pending, loaded.Features[1].Status);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ClaimReadyForCodingTurn_fails_when_nothing_to_claim()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-claim-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-claim-empty", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-claim-empty", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "g",
                Features = [new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.Pending }]
            });

            var claim = workflow.ClaimReadyForCodingTurn();
            Assert.False(claim.Ok);
            Assert.Contains("No ready", claim.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SetFeatureStatus_failed_increments_Attempt_and_keeps_findings_for_Summarize()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-fail-test", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-fail-test", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "g",
                Features = [new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.InProgress }]
            });

            workflow.SetFeatureStatus("f1", "failed", notes: "build error CS0246");
            var f = store.TryLoad()!.Features[0];
            Assert.Equal(1, f.Attempt);
            Assert.Contains("CS0246", CoderFeatureStore.Summarize(store.TryLoad()!), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    public void Sanitize_blocks_path_traversal(string featureId)
    {
        var sanitized = CoderFeatureStore.Sanitize(featureId);
        Assert.DoesNotContain("..", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain('/', sanitized);
        Assert.DoesNotContain('\\', sanitized);
    }

    [Fact]
    public void Generator_actor_cannot_mark_done()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-gen-actor", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-gen-actor", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "g",
                Features = [new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.InProgress }]
            });

            var msg = workflow.SetFeatureStatus(
                "f1", "done", "VERDICT: PASS", FeatureStatusActor.Generator);
            Assert.Contains("generator may only", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CoderFeatureStatus.InProgress, store.TryLoad()!.Features[0].Status);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Generator_actor_cannot_reopen_done_as_pending()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-gen-done-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-gen-done", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-gen-done", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "g",
                Features =
                [
                    new CoderFeatureItem
                    {
                        Id = "f1",
                        Title = "A",
                        Status = CoderFeatureStatus.Done,
                        VerifiedAt = DateTimeOffset.UtcNow
                    }
                ]
            });

            var msg = workflow.SetFeatureStatus(
                "f1", "pending", null, FeatureStatusActor.Generator);
            Assert.Contains("ready|in_progress", msg, StringComparison.OrdinalIgnoreCase);
            var f = store.TryLoad()!.Features[0];
            Assert.Equal(CoderFeatureStatus.Done, f.Status);
            Assert.NotNull(f.VerifiedAt);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Planner_actor_cannot_mark_done()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-plan-actor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-plan-actor", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-plan-actor", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "g",
                Features = [new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.Ready }]
            });

            var msg = workflow.SetFeatureStatus(
                "f1", "done", "VERDICT: PASS", FeatureStatusActor.Planner);
            Assert.Contains("planner may only", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CoderFeatureStatus.Ready, store.TryLoad()!.Features[0].Status);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ClaimReady_auto_fails_stuck_in_progress_after_max_claims()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-stuck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-stuck", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-stuck", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "g",
                Features =
                [
                    new CoderFeatureItem
                    {
                        Id = "f1",
                        Title = "A",
                        Status = CoderFeatureStatus.InProgress,
                        InProgressClaims = CoderFeatureStatus.MaxInProgressClaims - 1
                    }
                ]
            });

            var claim = workflow.ClaimReadyForCodingTurn();
            Assert.False(claim.Ok);
            var f = store.TryLoad()!.Features[0];
            Assert.Equal(CoderFeatureStatus.Failed, f.Status);
            Assert.Equal(1, f.Attempt);
            Assert.Contains("auto-failed", f.LastFindings, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void PickNext_skips_features_at_MaxAttempts()
    {
        var doc = new CoderFeatureListDocument
        {
            Goal = "g",
            Features =
            [
                new CoderFeatureItem
                {
                    Id = "f1",
                    Title = "A",
                    Status = CoderFeatureStatus.Failed,
                    Attempt = CoderFeatureStatus.MaxAttempts
                },
                new CoderFeatureItem { Id = "f2", Title = "B", Status = CoderFeatureStatus.Pending }
            ]
        };

        Assert.Equal("f2", CoderFeatureStore.PickNext(doc)!.Id);
    }

    [Fact]
    public void ClaimReady_auto_fails_ready_when_coding_turn_budget_exhausted()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-budget", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-budget", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "g",
                Features =
                [
                    new CoderFeatureItem
                    {
                        Id = "f1",
                        Title = "A",
                        Status = CoderFeatureStatus.Ready,
                        CodingTurnCount = CoderFeatureStatus.MaxCodingTurns
                    }
                ]
            });

            var claim = workflow.ClaimReadyForCodingTurn();
            Assert.False(claim.Ok);
            var f = store.TryLoad()!.Features[0];
            Assert.Equal(CoderFeatureStatus.Failed, f.Status);
            Assert.Contains("coding-turn budget", f.LastFindings!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ReleaseReadyBatch_respects_wip_and_appends_resume_feedback()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-coder-rel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CoderFeatureStore(root);
            var doc = new CoderFeatureListDocument
            {
                Goal = "g",
                Features =
                [
                    new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.Pending },
                    new CoderFeatureItem { Id = "f2", Title = "B", Status = CoderFeatureStatus.Pending },
                    new CoderFeatureItem { Id = "f3", Title = "C", Status = CoderFeatureStatus.Pending }
                ]
            };
            store.Save(doc);

            var log = new StringBuilder();
            var n = FeatureListReleaseReadyNode.ReleaseReadyBatch(
                doc, store, budget: 2, resumeFeedback: "fix flaky", log);
            Assert.Equal(2, n);
            Assert.Equal(2, CoderFeatureStore.CountReady(doc));
            Assert.Contains("fix flaky", doc.Features[0].LastFindings, StringComparison.Ordinal);
            Assert.Contains("fix flaky", doc.Features[1].LastFindings, StringComparison.Ordinal);
            Assert.Equal(CoderFeatureStatus.Pending, doc.Features[2].Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ReleaseReadyBatch_returns_zero_when_ready_slots_full()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-coder-rel-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CoderFeatureStore(root);
            var doc = new CoderFeatureListDocument
            {
                Goal = "g",
                Features =
                [
                    new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.Ready },
                    new CoderFeatureItem { Id = "f2", Title = "B", Status = CoderFeatureStatus.Ready },
                    new CoderFeatureItem { Id = "f3", Title = "C", Status = CoderFeatureStatus.Pending }
                ]
            };
            store.Save(doc);

            var log = new StringBuilder();
            var n = FeatureListReleaseReadyNode.ReleaseReadyBatch(
                doc, store, budget: 2, resumeFeedback: null, log);
            Assert.Equal(0, n);
            Assert.Contains("No slots", log.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CoderFeatureStatus.Pending, doc.Features[2].Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void PickNext_skips_features_at_MaxCodingTurns()
    {
        var doc = new CoderFeatureListDocument
        {
            Goal = "g",
            Features =
            [
                new CoderFeatureItem
                {
                    Id = "f1",
                    Title = "A",
                    Status = CoderFeatureStatus.Failed,
                    Attempt = 1,
                    CodingTurnCount = CoderFeatureStatus.MaxCodingTurns
                },
                new CoderFeatureItem { Id = "f2", Title = "B", Status = CoderFeatureStatus.Pending }
            ]
        };

        Assert.Equal("f2", CoderFeatureStore.PickNext(doc)!.Id);
    }

    [Fact]
    public void ArchiveForReset_renames_under_store_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-coder-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CoderFeatureStore(root);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "old",
                SpecMarkdown = "# Spec",
                Features = [new CoderFeatureItem { Id = "f1", Title = "A" }]
            });

            var prefix = store.ArchiveForReset();
            Assert.NotNull(prefix);
            Assert.False(store.Exists);
            Assert.True(File.Exists(prefix!));
            Assert.True(File.Exists(prefix + ".spec.md"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ShortRunningFailGate_blocks_after_max_cycles()
    {
        using (Orbit.Agents.Hosting.OrbitRunContext.Push("run-gate", "session-gate-test"))
        {
            ShortRunningFailGate.ResetAfterUserOk();
            Assert.Contains("0/", ShortRunningFailGate.Status(), StringComparison.Ordinal);
            Assert.Contains("r:run-gate", ShortRunningFailGate.Status(), StringComparison.Ordinal);
            Assert.Contains("may handoff", ShortRunningFailGate.NoteFail(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("last allowed", ShortRunningFailGate.NoteFail(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("BLOCKED", ShortRunningFailGate.NoteFail(), StringComparison.Ordinal);
            ShortRunningFailGate.NotePass();
            Assert.Contains("0/", ShortRunningFailGate.Status(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShortRunningFailGate_scopes_per_RunId_not_SessionId()
    {
        const string session = "session-shared";
        using (Orbit.Agents.Hosting.OrbitRunContext.Push("run-a", session))
        {
            ShortRunningFailGate.ResetAfterUserOk();
            ShortRunningFailGate.NoteFail();
            ShortRunningFailGate.NoteFail();
            Assert.Contains("2/", ShortRunningFailGate.Status(), StringComparison.Ordinal);
        }

        using (Orbit.Agents.Hosting.OrbitRunContext.Push("run-b", session))
        {
            Assert.Contains("0/", ShortRunningFailGate.Status(), StringComparison.Ordinal);
            Assert.Contains("r:run-b", ShortRunningFailGate.Status(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunAsync_without_resume_refuses_existing_FeatureList()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "orbit-coder-exists-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var workflow = new FeatureListWorkflow("coder-exists", contentRoot);
            var store = CoderFeatureStore.ForAgent("coder-exists", contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "old",
                Features = [new CoderFeatureItem { Id = "f1", Title = "A", Status = CoderFeatureStatus.Done }]
            });

            var msg = await workflow.RunAsync("new goal", null, CancellationToken.None);
            Assert.Contains("already exists", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("resume=true", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("old", store.TryLoad()!.Goal);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }
}
