using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.Sugary;
using Domain.Tools.Sugary.Vfs;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// /sugary is the log of the person the turn is for, in every household. The words a model reads about
// it — the skill, the mount and the README it serves — were written about the home's one person
// ("her log", "she has type 1 diabetes") and named another of the home's people outright, and a member
// of a household of their own was told about "her" log, which was his. They speak of the person
// asking, whoever that is.
public sealed partial class SugaryWordsTests
{
    [GeneratedRegex(@"\b(she|her|hers|herself|fran)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnePerson();

    public static TheoryData<string, string> Texts()
    {
        var mount = new SugaryFileSystem(Mock.Of<ISugaryLog>(), TimeZoneInfo.Utc, Mock.Of<ITableEngine>());
        return new TheoryData<string, string>
        {
            { "the skill's description", SugarySkill.Description },
            { "the skill's body", SugarySkill.Body },
            { "the README", SugaryReadme.For(TimeZoneInfo.Utc) },
            { "the mount's description", mount.DescribeMount },
            { "the read tool's description", mount.DescribeRead },
            { "the info tool's description", mount.DescribeInfo },
            { "the glob tool's description", mount.DescribeGlob },
            { "the query tool's description", mount.DescribeQuery },
            { "the blob read's description", mount.DescribeBlobRead }
        };
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void WhatAModelReadsAboutTheLog_SpeaksOfThePersonAsking_NotOfOneOfTheHomesPeople(string what, string text) =>
        OnePerson().Matches(text).Select(m => m.Value).ShouldBeEmpty($"{what} speaks of one person");
}