// SPDX-License-Identifier: GPL-2.0-only
//
// Realtek RTL810xE bring-up support for Cosmos Gen 3.
//
// Hardware/register reference and any adapted Linux material:
//   Linux drivers/net/ethernet/realtek/r8169_main.c
//   https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/drivers/net/ethernet/realtek/r8169_main.c
//
// This first stage deliberately stops after PCI/MMIO/MAC/link discovery. The
// DMA descriptor implementation will be added only after it is verified on
// the target Dell hardware; it must not be exposed as a working network
// device before then.

using Cosmos.Kernel.Core;
using Cosmos.Kernel.Core.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Pci;

namespace Cosmos.Kernel.HAL.X64.Devices.Network;

/// <summary>
/// RTL810xE PCI bring-up driver. This is intentionally not packet-ready yet.
/// </summary>
internal sealed class RealtekRtl810xE : PciDevice, INetworkDevice
{
    private const uint ChipCommand = 0x37;
    private const byte CommandReset = 0x10;
    private const byte CommandRxEnable = 0x08;
    private const byte CommandTxEnable = 0x04;
    private const uint IntrMask = 0x3C;
    private const uint IntrStatus = 0x3E;
    private const uint MacAddressRegister = 0x00;
    private const uint PhyStatus = 0x6C;
    private const uint PhyLinkUp = 1 << 1;

    private readonly ulong _mmioBase;
    private MACAddress? _macAddress;
    private bool _initialized;
    private bool _linkUp;

    public RealtekRtl810xE(uint bus, uint slot, uint function) : base(bus, slot, function)
    {
        _mmioBase = GetBar64Address(0);
    }

    public static RealtekRtl810xE? FindAndCreate()
    {
        PciDevice? device = PciManager.GetDevice(
            Pci.Enums.VendorId.Realtek,
            Pci.Enums.DeviceId.Rtl810xE);

        if (device == null || device.Claimed)
        {
            return null;
        }

        return new RealtekRtl810xE(device.Bus, device.Slot, device.Function);
    }

    string INetworkDevice.Name => "Realtek RTL810xE (bring-up)";
    public MACAddress MacAddress => _macAddress ?? throw new InvalidOperationException("Realtek MAC is not initialized");
    public bool LinkUp => _linkUp;
    public bool Ready => false;
    public PacketReceivedHandler? OnPacketReceived { get; set; }

    public void Initialize()
    {
        if (_initialized || _mmioBase == 0)
        {
            Serial.WriteString("[RTL810xE] No usable MMIO BAR0; leaving device unbound\n");
            return;
        }

        EnableMemory(true);
        EnableBusMaster(true);
        Claimed = true;

        Serial.WriteString("[RTL810xE] MMIO base 0x");
        Serial.WriteHex(_mmioBase);
        Serial.WriteString("\n");

        // Mask interrupts and request the documented software reset before
        // touching the MAC registers.
        Write16(IntrMask, 0);
        Write16(IntrStatus, 0xFFFF);
        Write8(ChipCommand, CommandReset);
        bool resetComplete = false;
        for (int i = 0; i < 100000; i++)
        {
            if ((Read8(ChipCommand) & CommandReset) == 0)
            {
                resetComplete = true;
                break;
            }
        }

        if (!resetComplete)
        {
            Serial.WriteString("[RTL810xE] Reset timed out; leaving device unbound\n");
            Claimed = false;
            return;
        }

        byte[] mac = new byte[6];
        for (uint i = 0; i < mac.Length; i++)
        {
            mac[i] = Read8(MacAddressRegister + i);
        }

        _macAddress = new MACAddress(mac);
        _linkUp = (Read8(PhyStatus) & PhyLinkUp) != 0;
        _initialized = true;

        Serial.WriteString("[RTL810xE] MAC ");
        Serial.WriteString(_macAddress.ToString());
        Serial.WriteString(" link ");
        Serial.WriteString(_linkUp ? "UP" : "DOWN");
        Serial.WriteString("; DMA packet path is not enabled yet\n");
    }

    public bool Send(byte[] data, int length) => false;

    public void Enable()
    {
        if (!_initialized)
        {
            return;
        }

        Write8(ChipCommand, (byte)(CommandRxEnable | CommandTxEnable));
    }

    public void Disable()
    {
        if (_initialized)
        {
            Write8(ChipCommand, 0);
        }
    }

    private byte Read8(uint offset) => Native.MMIO.Read8(_mmioBase + offset);
    private ushort Read16(uint offset) => Native.MMIO.Read16(_mmioBase + offset);
    private void Write8(uint offset, byte value) => Native.MMIO.Write8(_mmioBase + offset, value);
    private void Write16(uint offset, ushort value) => Native.MMIO.Write16(_mmioBase + offset, value);
}
