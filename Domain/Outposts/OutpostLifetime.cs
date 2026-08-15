namespace Domain.Outposts;

// One decision written as two numbers, which is why they are here together rather than in either
// side's settings. The machine asks again every interval; the hub forgets it after the expiry. The
// expiry is three intervals, so two lost keepalives — a suspended laptop, a flapping VPN — cost the
// outpost nothing, and the third means it really is gone.
//
// Not settings: a deployment cannot tune one of these without the other, and a hub and a machine
// disagreeing about them is the one way this can go quietly wrong.
public static class OutpostLifetime
{
    public static TimeSpan KeepAliveInterval => TimeSpan.FromSeconds(30);

    public static TimeSpan Expiry => TimeSpan.FromSeconds(90);
}