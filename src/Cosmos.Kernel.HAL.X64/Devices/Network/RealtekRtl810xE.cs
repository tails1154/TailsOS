// SPDX-License-Identifier: GPL-2.0-only
//
// Realtek RTL810xE packet driver for Cosmos Gen 3.
// Hardware/register reference and adapted Linux material are documented in
// THIRD_PARTY/REALTEK-R8169.md.

using Cosmos.Kernel.Core;
using Cosmos.Kernel.Core.IO;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Pci;

namespace Cosmos.Kernel.HAL.X64.Devices.Network;

/// <summary>Polled RTL810xE/RTL8168-compatible Ethernet driver.</summary>
internal sealed unsafe class RealtekRtl810xE : PciDevice, INetworkDevice
{
    private const uint Mac = 0x00;
    private const uint TxDescStart = 0x20;
    private const uint ChipCommand = 0x37;
    private const uint TxPoll = 0x38;
    private const uint IntrMask = 0x3C;
    private const uint IntrStatus = 0x3E;
    private const uint TxConfig = 0x40;
    private const uint RxConfig = 0x44;
    private const uint Cfg9346 = 0x50;
    private const uint RxMaxSize = 0xDA;
    private const uint CPlusCommand = 0xE0;
    private const uint RxDescStart = 0xE4;
    private const uint PhyStatus = 0x6C;

    private const byte Reset = 0x10;
    private const byte RxEnable = 0x08;
    private const byte TxEnable = 0x04;
    private const byte Cfg9346Unlock = 0xC0;
    private const byte TxPollNormalPriority = 0x40;
    private const uint DescOwn = 1u << 31;
    private const uint DescEndOfRing = 1u << 30;
    private const uint DescFirst = 1u << 29;
    private const uint DescLast = 1u << 28;
    private const uint DescLengthMask = 0x1FFF;
    private const uint LinkUpBit = 1u << 1;

    private const int RingCount = 64;
    private const int BufferSize = 2048;
    private const int MinimumEthernetFrame = 60;

    private struct Descriptor
    {
        public uint Options1;
        public uint Options2;
        public ulong BufferAddress;
    }

    private readonly ulong _mmioBase;
    private MACAddress? _macAddress;
    private Descriptor* _rxDescriptors;
    private Descriptor* _txDescriptors;
    private byte** _rxBuffers;
    private byte** _txBuffers;
    private int _rxIndex;
    private int _txIndex;
    private bool _initialized;
    private bool _enabled;
    private bool _linkUp;

    public RealtekRtl810xE(uint bus, uint slot, uint function) : base(bus, slot, function)
    {
        _mmioBase = GetBar64Address(0);
    }

    public static RealtekRtl810xE? FindAndCreate()
    {
        PciDevice? device = PciManager.GetDevice(Pci.Enums.VendorId.Realtek, Pci.Enums.DeviceId.Rtl810xE);
        device ??= PciManager.GetDevice(Pci.Enums.VendorId.Realtek, Pci.Enums.DeviceId.Rtl8139);
        return device is null || device.Claimed ? null : new RealtekRtl810xE(device.Bus, device.Slot, device.Function);
    }

    string INetworkDevice.Name => "Realtek RTL810xE";
    public MACAddress MacAddress => _macAddress ?? throw new InvalidOperationException("Realtek MAC is not initialized");
    public bool LinkUp => _linkUp;
    public bool Ready => _initialized && _enabled;
    public PacketReceivedHandler? OnPacketReceived { get; set; }

    public void Initialize()
    {
        if (_initialized || _mmioBase == 0)
        {
            return;
        }

        EnableMemory(true);
        EnableBusMaster(true);
        Claimed = true;
        Write8(Cfg9346, Cfg9346Unlock);
        Write16(IntrMask, 0);
        Write16(IntrStatus, 0xFFFF);
        Write8(ChipCommand, Reset);

        bool resetComplete = false;
        for (int i = 0; i < 100000; i++)
        {
            if ((Read8(ChipCommand) & Reset) == 0)
            {
                resetComplete = true;
                break;
            }
        }

        if (!resetComplete)
        {
            Serial.WriteString("[RTL810xE] reset timed out\n");
            Claimed = false;
            return;
        }

        byte[] mac = new byte[6];
        for (uint i = 0; i < 6; i++)
        {
            mac[i] = Read8(Mac + i);
        }

        _macAddress = new MACAddress(mac);
        _linkUp = (Read8(PhyStatus) & LinkUpBit) != 0;
        if (!InitializeRings())
        {
            Serial.WriteString("[RTL810xE] DMA ring allocation failed\n");
            Claimed = false;
            return;
        }

        Write8(RxMaxSize, 0x08);
        Write32(RxConfig, 0x0000E70E);
        Write32(TxConfig, 0x03000700);
        Write16(CPlusCommand, 0x0000);
        Write8(ChipCommand, (byte)(RxEnable | TxEnable));
        _enabled = true;
        _initialized = true;
        Write8(Cfg9346, 0x00);

        Serial.WriteString("[RTL810xE] packet DMA ready, MAC ");
        Serial.WriteString(_macAddress.ToString());
        Serial.WriteString(" link ");
        Serial.WriteString(_linkUp ? "UP\n" : "DOWN\n");
    }

    private bool InitializeRings()
    {
        int bytes = RingCount * sizeof(Descriptor);
        _rxDescriptors = Align((Descriptor*)MemoryOp.Alloc((uint)(bytes + 256)), 256);
        _txDescriptors = Align((Descriptor*)MemoryOp.Alloc((uint)(bytes + 256)), 256);
        _rxBuffers = (byte**)MemoryOp.Alloc((uint)(RingCount * sizeof(byte*)));
        _txBuffers = (byte**)MemoryOp.Alloc((uint)(RingCount * sizeof(byte*)));
        if (_rxDescriptors == null || _txDescriptors == null || _rxBuffers == null || _txBuffers == null)
        {
            return false;
        }

        MemoryOp.MemSet((byte*)_rxDescriptors, 0, bytes);
        MemoryOp.MemSet((byte*)_txDescriptors, 0, bytes);
        for (int i = 0; i < RingCount; i++)
        {
            _rxBuffers[i] = (byte*)MemoryOp.Alloc(BufferSize);
            _txBuffers[i] = (byte*)MemoryOp.Alloc(BufferSize);
            if (_rxBuffers[i] == null || _txBuffers[i] == null)
            {
                return false;
            }

            _rxDescriptors[i].BufferAddress = Physical((ulong)_rxBuffers[i]);
            _rxDescriptors[i].Options1 = DescOwn | (i == RingCount - 1 ? DescEndOfRing : 0);
            _txDescriptors[i].BufferAddress = Physical((ulong)_txBuffers[i]);
            _txDescriptors[i].Options1 = i == RingCount - 1 ? DescEndOfRing : 0;
        }

        ulong rx = Physical((ulong)_rxDescriptors);
        ulong tx = Physical((ulong)_txDescriptors);
        Write32(RxDescStart, (uint)rx);
        Write32(RxDescStart + 4, (uint)(rx >> 32));
        Write32(TxDescStart, (uint)tx);
        Write32(TxDescStart + 4, (uint)(tx >> 32));
        _rxIndex = 0;
        _txIndex = 0;
        return true;
    }

    public void Poll()
    {
        if (!_initialized || !_enabled)
        {
            return;
        }

        while ((_rxDescriptors[_rxIndex].Options1 & DescOwn) == 0)
        {
            Descriptor* descriptor = &_rxDescriptors[_rxIndex];
            uint options = descriptor->Options1;
            int length = (int)(options & DescLengthMask);
            if ((options & DescFirst) != 0 && (options & DescLast) != 0 && length >= 14 && length <= BufferSize)
            {
                byte[] packet = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    packet[i] = _rxBuffers[_rxIndex][i];
                }

                OnPacketReceived?.Invoke(packet, length);
            }

            descriptor->Options1 = DescOwn | (_rxIndex == RingCount - 1 ? DescEndOfRing : 0);
            _rxIndex = (_rxIndex + 1) % RingCount;
        }
    }

    public bool Send(byte[] data, int length)
    {
        if (!Ready || data is null || length <= 0 || length > BufferSize)
        {
            return false;
        }

        Descriptor* descriptor = &_txDescriptors[_txIndex];
        if ((descriptor->Options1 & DescOwn) != 0)
        {
            return false;
        }

        int frameLength = length < MinimumEthernetFrame ? MinimumEthernetFrame : length;
        for (int i = 0; i < frameLength; i++)
        {
            _txBuffers[_txIndex][i] = i < length ? data[i] : (byte)0;
        }

        descriptor->Options2 = 0;
        descriptor->Options1 = DescOwn | DescFirst | DescLast | (uint)frameLength |
            (_txIndex == RingCount - 1 ? DescEndOfRing : 0);
        Write8(TxPoll, TxPollNormalPriority);
        _txIndex = (_txIndex + 1) % RingCount;
        return true;
    }

    public void Enable()
    {
        if (_initialized)
        {
            Write8(ChipCommand, (byte)(RxEnable | TxEnable));
            _enabled = true;
        }
    }

    public void Disable()
    {
        if (_initialized)
        {
            Write8(ChipCommand, 0);
            _enabled = false;
        }
    }

    private static Descriptor* Align(Descriptor* pointer, uint alignment) =>
        (Descriptor*)(((ulong)pointer + alignment - 1) & ~(alignment - 1));

    private static ulong Physical(ulong address) => PageAllocator.VirtualToPhysical(address);
    private byte Read8(uint offset) => Native.MMIO.Read8(_mmioBase + offset);
    private ushort Read16(uint offset) => Native.MMIO.Read16(_mmioBase + offset);
    private void Write8(uint offset, byte value) => Native.MMIO.Write8(_mmioBase + offset, value);
    private void Write16(uint offset, ushort value) => Native.MMIO.Write16(_mmioBase + offset, value);
    private void Write32(uint offset, uint value) => Native.MMIO.Write32(_mmioBase + offset, value);
}
