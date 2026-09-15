// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using Cosmos.Kernel.Boot.Limine;
using Cosmos.Kernel.Core;
using Cosmos.Kernel.Core.IO;
using Cosmos.Kernel.HAL.Pci;
using Cosmos.Kernel.HAL.Pci.Enums;

namespace Cosmos.Kernel.HAL.X64.Devices.Usb;

/// <summary>
/// First-stage xHCI bring-up. This deliberately only discovers and describes
/// the controller; USB transfers are added separately so a failed controller
/// cannot take down the existing PS/2 input path.
/// </summary>
internal sealed unsafe class XhciController
{
    private const byte UsbClass = 0x0C;
    private const byte XhciProgrammingInterface = 0x30;
    private const ulong HcsParams1Offset = 0x04;
    private const ulong HccParams1Offset = 0x10;
    private const ulong DboffOffset = 0x14;
    private const ulong RtsoffOffset = 0x18;

    private readonly PciDevice _pci;
    private readonly ulong _mmio;

    public byte CapabilityLength { get; }
    public ushort InterfaceVersion { get; }
    public uint MaxSlots { get; }
    public uint MaxPorts { get; }
    public uint DoorbellOffset { get; }
    public uint RuntimeOffset { get; }

    private XhciController(PciDevice pci, ulong mmio)
    {
        _pci = pci;
        _mmio = mmio;
        CapabilityLength = Native.MMIO.Read8(mmio);
        InterfaceVersion = Native.MMIO.Read16(mmio + 2);

        uint hcsParams1 = Native.MMIO.Read32(mmio + HcsParams1Offset);
        MaxSlots = hcsParams1 & 0xFF;
        MaxPorts = (hcsParams1 >> 24) & 0xFF;
        DoorbellOffset = Native.MMIO.Read32(mmio + DboffOffset);
        RuntimeOffset = Native.MMIO.Read32(mmio + RtsoffOffset);
    }

    public static XhciController? Find()
    {
        if (PciManager.Devices is null)
        {
            return null;
        }

        for (uint i = 0; i < PciManager.Count; i++)
        {
            PciDevice device = PciManager.Devices[i];
            if (device.ClassCode != UsbClass ||
                device.Subclass != (byte)SubclassId.UniversalSerialBus ||
                device.ProgIf != XhciProgrammingInterface)
            {
                continue;
            }

            ulong bar = device.GetBar64Address(0);
            if (bar == 0)
            {
                Serial.WriteString("[xHCI] Controller found without a usable BAR0\n");
                continue;
            }

            PlatformHAL.Initializer?.EnsureMmioMapped(bar);
            device.EnableBusMaster(true);
            device.EnableMemory(true);

            ulong hhdm = Limine.HHDM.Response != null ? Limine.HHDM.Response->Offset : 0;
            XhciController controller = new(device, bar + hhdm);
            Serial.WriteString("[xHCI] Controller ");
            Serial.WriteNumber(device.Bus);
            Serial.WriteString(":");
            Serial.WriteNumber(device.Slot);
            Serial.WriteString(".");
            Serial.WriteNumber(device.Function);
            Serial.WriteString(" VID=0x");
            Serial.WriteHex(device.VendorId);
            Serial.WriteString(" DID=0x");
            Serial.WriteHex(device.DeviceId);
            Serial.WriteString(" v");
            Serial.WriteNumber(controller.InterfaceVersion >> 8);
            Serial.WriteString(".");
            Serial.WriteNumber(controller.InterfaceVersion & 0xFF);
            Serial.WriteString(" slots=");
            Serial.WriteNumber(controller.MaxSlots);
            Serial.WriteString(" ports=");
            Serial.WriteNumber(controller.MaxPorts);
            Serial.WriteString("\n");
            return controller;
        }

        Serial.WriteString("[xHCI] No USB 3 host controller found\n");
        return null;
    }
}
