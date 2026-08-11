using Domain.DTOs.Printing;
using Infrastructure.Clients.Printer;
using SharpIpp.Protocol.Models;
using Shouldly;
using Xunit;

namespace Tests.Unit.Infrastructure.Printing;

public class IppJobStateMapperTests
{
    [Theory]
    [InlineData(JobState.Pending, PrintJobState.Pending)]
    [InlineData(JobState.PendingHeld, PrintJobState.Pending)]
    [InlineData(JobState.Processing, PrintJobState.Processing)]
    [InlineData(JobState.ProcessingStopped, PrintJobState.Processing)]
    [InlineData(JobState.Completed, PrintJobState.Completed)]
    [InlineData(JobState.Canceled, PrintJobState.Canceled)]
    [InlineData(JobState.Aborted, PrintJobState.Aborted)]
    public void Map_TranslatesKnownStates(JobState ipp, PrintJobState expected)
    {
        IppJobStateMapper.Map(ipp).ShouldBe(expected);
    }

    [Theory]
    [InlineData(JobState.Pending, true)]
    [InlineData(JobState.Processing, true)]
    [InlineData(JobState.Completed, false)]
    public void IsActive_TrueOnlyForPendingOrProcessing(JobState ipp, bool active)
    {
        IppJobStateMapper.IsActive(ipp).ShouldBe(active);
    }
}