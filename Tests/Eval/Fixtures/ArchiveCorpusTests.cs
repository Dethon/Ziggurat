using Shouldly;

namespace Tests.Eval.Fixtures;

// The scale rungs' corpus: a food bank's site, too big to read whole. What is pinned is what
// makes it a test of research rather than of reading — the answers are spread over many pages
// or sit on one page among sixty, no page prints what a rung asks for, and the search does not
// hand the archive over.
public class ArchiveCorpusTests
{
    private static IEnumerable<string> Bulletins(int year) =>
        ArchiveCorpus.Pages.Where(page => page.Key.StartsWith($"/banco/boletines/{year}-")).Select(page => page.Value);

    [Fact]
    public void TheSite_IsTooBigToReadInOneRungsCalls()
    {
        ArchiveCorpus.Pages.Count.ShouldBeGreaterThanOrEqualTo(55);
        // Fifty-five months and one of them twice: the bulletin that was republished corrected.
        ArchiveCorpus.Pages.Keys.Count(path => path.StartsWith("/banco/boletines/")).ShouldBe(56);
    }

    [Fact]
    public void TheYearWithAReport_IsTotalledOnIt_AndTheYearWithout_IsTotalledNowhere()
    {
        ArchiveCorpus.Pages["/banco/memoria-2024"].ShouldContain(SolarCorpus.Es(ArchiveCorpus.Kilos2024));
        // The sum of twelve bulletins, and on none of the site's pages: a reply carrying it
        // read the twelve.
        ArchiveCorpus.Pages.Values.ShouldAllBe(page => !page.Contains(SolarCorpus.Es(ArchiveCorpus.Kilos2025)));
        Bulletins(2025).Count().ShouldBe(12);
        ArchiveCorpus.Kilos2025.ShouldBe(ArchiveCorpus.MonthlyKilos.Skip(12).Take(12).Sum());
        ArchiveCorpus.Kilos2024.ShouldBe(ArchiveCorpus.MonthlyKilos.Take(12).Sum());
    }

    [Fact]
    public void NoTwoBulletins_ReadAlike()
    {
        // Thirty-one copies of one page with a number changed read as a site that was
        // mispublished, and a reply spends itself on saying so.
        var bodies = ArchiveCorpus.Pages
            .Where(page => page.Key.StartsWith("/banco/boletines/"))
            .Select(page => System.Text.RegularExpressions.Regex.Replace(page.Value, @"\d", "#"))
            .ToList();

        bodies.Distinct().Count().ShouldBe(bodies.Count);
    }

    [Fact]
    public void HomeDelivery_FirstAppearsInTheBulletinOfTheMonthItBegan()
    {
        var start = ArchiveCorpus.DeliveryStarted;
        var mentioning = ArchiveCorpus.Pages
            .Where(page => page.Key.StartsWith("/banco/boletines/") && page.Value.Contains("eparto a domicilio"))
            .Select(page => page.Key)
            .Order()
            .ToList();

        mentioning.First().ShouldBe($"/banco/boletines/{start.Year}-{start.Month:00}");
        ArchiveCorpus.Pages[$"/banco/boletines/{start.Year}-12"].ShouldContain($"{ArchiveCorpus.DeliveryAtYearEnd} familias");
    }

    [Fact]
    public void TodaysVolunteers_AreOnOneNoticeAmongAllThePages_AndTheLatestBulletinSaysMore()
    {
        var carrying = ArchiveCorpus.Pages
            .Where(page => page.Value.Contains($"{ArchiveCorpus.VolunteersNow} voluntari"))
            .Select(page => page.Key)
            .ToList();

        carrying.ShouldHaveSingleItem().ShouldStartWith("/banco/avisos/");
        ArchiveCorpus.Pages["/banco/boletines/2026-07"].ShouldContain($"{ArchiveCorpus.VolunteersInLatestBulletin} personas");
        ArchiveCorpus.VolunteersNow.ShouldBeLessThan(ArchiveCorpus.VolunteersInLatestBulletin);
    }

    // The older half of the archive is where reading everything stops being enough: what is
    // on the pages has to be converted, and one page has to be left out.
    [Fact]
    public void TheFirstHalfOf2022_IsCountedInBoxes_AndTheRestInKilos()
    {
        Enumerable.Range(1, 6).Select(month => ArchiveCorpus.Pages[$"/banco/boletines/2022-{month:00}"])
            .ShouldAllBe(page => page.Contains("cajas") && !page.Contains(" kg"));
        ArchiveCorpus.Pages["/banco/boletines/2022-07"].ShouldContain(" kg");
        ArchiveCorpus.Pages["/banco/boletines/2022-07"].ShouldContain($"{ArchiveCorpus.BoxKilos} kg");
        ArchiveCorpus.EarlyKilos.Take(6).ShouldAllBe(kilos => kilos % ArchiveCorpus.BoxKilos == 0);
    }

    [Fact]
    public void OneBulletin_WasRepublishedCorrected_AndTheYearCountsItOnceAtTheCorrectedFigure()
    {
        var corrected = ArchiveCorpus.EarlyKilos[16];
        ArchiveCorpus.Pages["/banco/boletines/2023-05-corregido"].ShouldContain(SolarCorpus.Es(corrected));
        ArchiveCorpus.Pages["/banco/boletines/2023-05"].ShouldContain(SolarCorpus.Es(ArchiveCorpus.May2023AsFirstPublished));
        ArchiveCorpus.Pages["/banco/boletines/2023-05"].ShouldNotContain(SolarCorpus.Es(corrected));
        ArchiveCorpus.Kilos2023.ShouldBe(ArchiveCorpus.EarlyKilos.Skip(12).Sum());
        ArchiveCorpus.Kilos2022.ShouldBe(ArchiveCorpus.EarlyKilos.Take(12).Sum());
    }

    [Fact]
    public void TheFourYearTotal_IsOnNoPage()
    {
        ArchiveCorpus.KilosSince2022.ShouldBe(
            ArchiveCorpus.Kilos2022 + ArchiveCorpus.Kilos2023 + ArchiveCorpus.Kilos2024 + ArchiveCorpus.Kilos2025);
        new[] { ArchiveCorpus.KilosSince2022, ArchiveCorpus.Kilos2022, ArchiveCorpus.Kilos2023 }
            .ShouldAllBe(total => ArchiveCorpus.Pages.Values.All(page => !page.Contains(SolarCorpus.Es(total))));
    }

    [Fact]
    public void TheFirstMonthOverTheLine_IsADecemberLongBeforeTheTrendGetsThere()
    {
        // The series rises slowly and jumps every December. The first month over the line is a
        // campaign month two winters before an ordinary one reaches it: halving the archive to
        // find where the trend crosses lands eighteen months late.
        var all = ArchiveCorpus.EarlyKilos.Concat(ArchiveCorpus.MonthlyKilos).ToList();
        var first = all.FindIndex(kilos => kilos > ArchiveCorpus.Line);
        var firstOrdinary = all.Select((kilos, index) => (kilos, index))
            .First(month => month.kilos > ArchiveCorpus.Line && month.index % 12 != 11).index;

        first.ShouldBe(11);
        ArchiveCorpus.FirstOverTheLine.ShouldBe((2022, 12));
        ArchiveCorpus.KilosFirstOverTheLine.ShouldBe(all[11]);
        firstOrdinary.ShouldBeGreaterThan(first + 12);
    }

    [Fact]
    public void TheBoard_DatesEveryNotice_SoALaterOneCanBeSeenToBeLater()
    {
        // The notice that matters is not about volunteers by its title. What tells a reader to
        // open it is its date beside the date the last bulletin was closed.
        ArchiveCorpus.Pages["/banco/avisos"].ShouldContain("6 de agosto de 2026 — Cierre del local de la calle Mayor");
        ArchiveCorpus.Pages["/banco/boletines/2026-07"].ShouldContain("cerrado el último día de julio de 2026");
    }

    [Fact]
    public void TheSearch_ReturnsABulletinOnlyToAQueryThatNamesItsYear()
    {
        // Otherwise one search for the food bank is the whole archive in a list.
        ArchiveCorpus.Search("http://site")
            .Where(entry => entry.Url.Contains("/banco/boletines/"))
            .ShouldAllBe(entry => entry.Requires != null && entry.Requires.Count == 1);
    }
}