using MarketHub.Modules.Catalog.Features;
using MarketHub.Modules.Finance.Features;
using MarketHub.Modules.Identity.Features;
using MarketHub.Modules.Notifications.Features;
using Quartz;
using Quartz.Spi;

namespace MarketHub.Api;

/// <summary>
/// Central Quartz registration (composition root): all recurring jobs with their cadences.
/// Jobs are DI-constructed (each execution gets its own scope via the Quartz DI job factory),
/// are idempotent, and log their own failures — a crashed run never takes the scheduler down.
/// </summary>
public static class QuartzSetup
{
    public static IServiceCollection AddMarketHubQuartz(this IServiceCollection services)
    {
        services.AddQuartz(q =>
        {
            // stock reservations past their TTL → Expired (1 min)
            q.AddJob<ReservationSweeperJob>(j => j.WithIdentity(ReservationSweeperJob.Key).StoreDurably());
            q.AddTrigger(t => t.ForJob(ReservationSweeperJob.Key).WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));

            // expired flash-sale prices cleared so listings never show stale discounts (1 min)
            q.AddJob<FlashSaleSchedulerJob>(j => j.WithIdentity(FlashSaleSchedulerJob.Key).StoreDurably());
            q.AddTrigger(t => t.ForJob(FlashSaleSchedulerJob.Key).WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));

            // abandoned-cart SMS reminders (30 min)
            q.AddJob<AbandonedCartReminderJob>(j => j.WithIdentity(AbandonedCartReminderJob.Key).StoreDurably());
            q.AddTrigger(t => t.ForJob(AbandonedCartReminderJob.Key).WithSimpleSchedule(s => s.WithIntervalInMinutes(30).RepeatForever()));

            // expired vendor documents → suspend vendors with expired REQUIRED docs + notify (1h)
            q.AddJob<DocumentExpiryMonitorJob>(j => j.WithIdentity(DocumentExpiryMonitorJob.Key).StoreDurably());
            q.AddTrigger(t => t.ForJob(DocumentExpiryMonitorJob.Key).WithSimpleSchedule(s => s.WithIntervalInHours(1).RepeatForever()));

            // payout SLA watchdog — flags 12h breaches, never auto-pays (15 min)
            q.AddJob<OverduePayoutJob>(j => j.WithIdentity(OverduePayoutJob.Key).StoreDurably());
            q.AddTrigger(t => t.ForJob(OverduePayoutJob.Key).WithSimpleSchedule(s => s.WithIntervalInMinutes(15).RepeatForever()));
        });
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        return services;
    }
}
