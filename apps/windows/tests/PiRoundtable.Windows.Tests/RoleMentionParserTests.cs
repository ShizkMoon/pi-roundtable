using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class RoleMentionParserTests
{
    private static readonly RoleItem[] Roles =
    [
        new("role.architect", "体系架构师", "long_term"),
        new("role.experience", "产品体验官", "long_term"),
        new("role.risk", "Risk Reviewer", "long_term"),
        new("role.art", "Art", "long_term"),
    ];

    [TestMethod]
    public void Resolves_only_explicit_mentions_in_source_order_and_deduplicates()
    {
        var result = RoleMentionParser.Parse(
            "@产品体验官 请先回答，＠体系架构师 补充；@产品体验官 不要重复。",
            Roles);

        CollectionAssert.AreEqual(
            new[] { "role.experience", "role.architect" },
            result.RoleIds.ToArray());
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void Does_not_match_role_name_prefix_or_email_address()
    {
        var result = RoleMentionParser.Parse(
            "联系 owner@example.com，@Artist 不是 @Art。",
            Roles);

        CollectionAssert.AreEqual(new[] { "role.art" }, result.RoleIds.ToArray());
        CollectionAssert.AreEqual(new[] { "Artist" }, result.UnknownMentions.ToArray());
    }

    [TestMethod]
    public void Rejects_unknown_and_ambiguous_mentions_instead_of_falling_back_to_all_roles()
    {
        var duplicateRoles = Roles.Concat(
            [new RoleItem("role.risk.duplicate", "Risk Reviewer", "temporary")]);

        var result = RoleMentionParser.Parse("@不存在角色 @Risk Reviewer", duplicateRoles);

        CollectionAssert.AreEqual(new[] { "不存在角色" }, result.UnknownMentions.ToArray());
        CollectionAssert.AreEqual(new[] { "Risk Reviewer" }, result.AmbiguousMentions.ToArray());
        Assert.IsFalse(result.IsValid);
    }

    [TestMethod]
    public void Ignores_mentions_inside_inline_and_fenced_code()
    {
        const string message = "`@产品体验官`\n```text\n@体系架构师\n```\n@Risk Reviewer please answer";

        var result = RoleMentionParser.Parse(message, Roles);

        CollectionAssert.AreEqual(new[] { "role.risk" }, result.RoleIds.ToArray());
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void Ignores_mentions_inside_markdown_fences_indented_by_up_to_three_spaces()
    {
        const string message = "  ```text\n@产品体验官\n  ```\n@体系架构师 answer";

        var result = RoleMentionParser.Parse(message, Roles);

        CollectionAssert.AreEqual(new[] { "role.architect" }, result.RoleIds.ToArray());
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void Does_not_match_a_short_role_prefix_before_a_hyphen()
    {
        var result = RoleMentionParser.Parse("@Art-reviewer answer", Roles);

        Assert.IsEmpty(result.RoleIds);
        CollectionAssert.AreEqual(new[] { "Art-reviewer" }, result.UnknownMentions.ToArray());
    }

    [TestMethod]
    public void Treats_a_standalone_at_sign_in_prose_as_punctuation()
    {
        var result = RoleMentionParser.Parse(
            "@体系架构师 只由你回答；正文里提到被 @ 角色时不应生成一个空点名。",
            Roles);

        CollectionAssert.AreEqual(new[] { "role.architect" }, result.RoleIds.ToArray());
        Assert.IsEmpty(result.UnknownMentions);
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void Merges_checked_responder_targets_with_typed_mentions_and_ignores_stale_ids()
    {
        var result = RoleMentionParser.Parse(
            "@产品体验官 answer",
            Roles,
            ["role.architect", "role.deleted"]);

        CollectionAssert.AreEqual(
            new[] { "role.architect", "role.experience" },
            result.RoleIds.ToArray());
        Assert.IsTrue(result.IsValid);
    }
}
