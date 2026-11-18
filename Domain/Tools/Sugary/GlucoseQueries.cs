namespace Domain.Tools.Sugary;

// The statements the README gives a reader of /sugary to copy, built here so the README and the tests
// that run them against a seeded log are the same text.
//
// The clinic's figures are shares of time, not of rows. Readings come at uneven intervals — about one
// a minute live, every five minutes on LibreLinkUp's curve, hourly from an import of long-term
// statistics — so a share of rows would weigh a stretch by how often it was read. Each reading stands
// for the minutes until the next one, up to 15, and the period's end ends the last; a longer gap is
// time not covered, an hourly mean's included. One weight serves every figure: time in each range,
// the mean, the spread and the coverage. The app's GlucoseStatistics computes the same figures the
// same way (its Cap), so what the agent says and what the app shows agree.
public static class GlucoseQueries
{
    // The longest a reading stands for; a longer gap is time with no reading.
    public const int MaxMinutesPerReading = 15;

    // The README's period: the last fourteen days up to now, a clinic's usual window.
    public const string LastFourteenDays = "now() - interval 14 day";
    public const string Now = "now()";

    // The clinic's figures for [since, until): since and until are SQL expressions (a TIMESTAMPTZ
    // literal reads on the mount's clock), low and high the person's own target range in mg/dL.
    public static string Statistics(string since, string until, int low, int high) =>
        $"""
         with period as (
           select {since} as since, {until} as until
         ),
         weighted as (
           select g.mg_dl,
                  least(date_diff('second', g.read_at, coalesce(lead(g.read_at) over (order by g.read_at), p.until)) / 60.0,
                        {MaxMinutesPerReading}) as minutes
           from glucose g, period p
           where g.read_at >= p.since and g.read_at < p.until
         ),
         summary as (
           select sum(minutes) as covered, sum(minutes * mg_dl) / sum(minutes) as mean from weighted
         )
         select
           round(100 * coalesce(sum(w.minutes) filter (where w.mg_dl < 54), 0) / s.covered, 1) as below_54_pct,
           round(100 * coalesce(sum(w.minutes) filter (where w.mg_dl < 70), 0) / s.covered, 1) as below_70_pct,
           round(100 * coalesce(sum(w.minutes) filter (where w.mg_dl >= 70 and w.mg_dl <= 180), 0) / s.covered, 1) as in_70_180_pct,
           round(100 * coalesce(sum(w.minutes) filter (where w.mg_dl >= {low} and w.mg_dl <= {high}), 0) / s.covered, 1) as in_target_pct,
           round(100 * coalesce(sum(w.minutes) filter (where w.mg_dl > 180), 0) / s.covered, 1) as above_180_pct,
           round(100 * coalesce(sum(w.minutes) filter (where w.mg_dl > 250), 0) / s.covered, 1) as above_250_pct,
           round(s.mean, 1) as mean_mg_dl,
           round(3.31 + 0.02392 * s.mean, 1) as gmi_pct,
           round(100 * sqrt(sum(w.minutes * power(w.mg_dl - s.mean, 2)) / s.covered) / s.mean, 1) as cv_pct,
           round(100 * s.covered / (date_diff('second', p.since, p.until) / 60.0), 1) as coverage_pct
         from weighted w, summary s, period p
         group by s.covered, s.mean, p.since, p.until
         """;

    // Each meal beside the glucose of the three hours after it: the first reading, the peak and when
    // it came, and the last.
    public const string AfterMeals =
        """
        select m.meal_id, m.eaten_at, m.carbs_g,
               arg_min(g.mg_dl, g.read_at) as first_mg_dl,
               max(g.mg_dl) as peak_mg_dl,
               arg_max(g.read_at, g.mg_dl) as peak_at,
               arg_max(g.mg_dl, g.read_at) as last_mg_dl
        from meals m
        join glucose g on g.read_at >= m.eaten_at and g.read_at < m.eaten_at + interval 3 hour
        group by m.meal_id, m.eaten_at, m.carbs_g
        order by m.eaten_at
        """;
}