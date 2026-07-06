using System;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Transport;

public class LedbatCongestionControlTests
{
    [Fact]
    public void Initial_Window_IsOnePacket()
    {
        var cc = new LedbatCongestionControl();
        cc.CongestionWindow.Should().BeGreaterOrEqualTo(LedbatCongestionControl.MinPacketSize);
    }

    [Fact]
    public void OnAck_LowDelay_IncreasesWindow()
    {
        var cc = new LedbatCongestionControl();
        int initial = cc.CongestionWindow;

        cc.UpdateBaseDelay(50_000);
        cc.OnAck(ackedBytes: 1400, inFlightBytes: 5000, delayUs: 50_000);

        cc.CongestionWindow.Should().BeGreaterThan(initial);
    }

    [Fact]
    public void OnAck_HighDelay_DecreasesWindow()
    {
        var cc = new LedbatCongestionControl();

        cc.UpdateBaseDelay(10_000);
        for (int i = 0; i < 20; i++)
            cc.OnAck(ackedBytes: 1400, inFlightBytes: 10000, delayUs: 10_000);

        int windowBefore = cc.CongestionWindow;

        cc.UpdateBaseDelay(10_000);
        cc.OnAck(ackedBytes: 1400, inFlightBytes: 10000, delayUs: 200_000);

        cc.CongestionWindow.Should().BeLessThan(windowBefore);
    }

    [Fact]
    public void OnPacketLoss_HalvesWindow()
    {
        var cc = new LedbatCongestionControl();
        cc.UpdateBaseDelay(10_000);
        for (int i = 0; i < 20; i++)
            cc.OnAck(ackedBytes: 1400, inFlightBytes: 10000, delayUs: 10_000);

        int windowBefore = cc.CongestionWindow;
        cc.OnPacketLoss();

        cc.CongestionWindow.Should().Be(Math.Max(
            (int)(windowBefore * cc.LossFactor),
            LedbatCongestionControl.MinPacketSize));
    }

    [Fact]
    public void OnTimeout_ResetsToMinimum()
    {
        var cc = new LedbatCongestionControl();
        cc.UpdateBaseDelay(10_000);
        for (int i = 0; i < 20; i++)
            cc.OnAck(ackedBytes: 1400, inFlightBytes: 10000, delayUs: 10_000);

        cc.OnTimeout();
        cc.CongestionWindow.Should().Be(LedbatCongestionControl.MinPacketSize);
    }

    [Fact]
    public void GetTimeoutMs_Initial_Is1000()
    {
        var cc = new LedbatCongestionControl();
        cc.GetTimeoutMs().Should().Be(1000);
    }

    [Fact]
    public void GetTimeoutMs_AfterRttSample_UsesRttFormula()
    {
        var cc = new LedbatCongestionControl();
        cc.UpdateRtt(80_000);
        cc.GetTimeoutMs().Should().BeGreaterOrEqualTo(500);
    }

    [Fact]
    public void GetTimeoutMs_NeverBelowMinimum()
    {
        var cc = new LedbatCongestionControl();
        cc.UpdateRtt(1_000);
        cc.GetTimeoutMs().Should().BeGreaterOrEqualTo(500);
    }

    [Fact]
    public void CanSend_RespectsWindowLimit()
    {
        var cc = new LedbatCongestionControl();
        int cwnd = cc.CongestionWindow;
        uint peerWnd = 65536;

        cc.CanSend(curWindowBytes: 0, packetSize: 150, peerWindowSize: peerWnd).Should().BeTrue();
        cc.CanSend(curWindowBytes: cwnd, packetSize: 1, peerWindowSize: peerWnd).Should().BeFalse();
    }

    [Fact]
    public void Window_NeverBelowMinPacketSize()
    {
        var cc = new LedbatCongestionControl();

        for (int i = 0; i < 50; i++)
            cc.OnPacketLoss();

        cc.CongestionWindow.Should().BeGreaterOrEqualTo(LedbatCongestionControl.MinPacketSize);
    }

    [Fact]
    public void BaseDelay_SlidingMinimum_Updates()
    {
        var cc = new LedbatCongestionControl();

        cc.UpdateBaseDelay(100_000);
        cc.UpdateBaseDelay(50_000);
        cc.UpdateBaseDelay(80_000);

        int before = cc.CongestionWindow;
        cc.OnAck(ackedBytes: 1400, inFlightBytes: 5000, delayUs: 60_000);
        cc.CongestionWindow.Should().BeGreaterThan(before);
    }
}
