/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2012 Michael Möller <mmoeller@openhardwaremonitor.org>
	
*/

using System;
using System.Globalization;
using System.Text;

namespace ccMonitor.Api.OpenHardwareMonitor.Hardware.LPC {
  internal class IT87XX : ISuperIO {
       
    private readonly ushort address;
    private readonly Chip chip;
    private readonly byte version;

    private readonly ushort gpioAddress;
    private readonly int gpioCount;

    private readonly ushort addressReg;
    private readonly ushort dataReg;

    private readonly float?[] voltages = new float?[0];
    private readonly float?[] temperatures = new float?[0];
    private readonly float?[] fans = new float?[0];
    private readonly float?[] controls = new float?[0];

    private readonly float voltageGain;
    private readonly bool has16bitFanCounter;
   
    // Consts
    private const byte ITE_VENDOR_ID = 0x90;
       
    // Environment Controller
    private const byte ADDRESS_REGISTER_OFFSET = 0x05;
    private const byte DATA_REGISTER_OFFSET = 0x06;

    // Environment Controller Registers    
    private const byte CONFIGURATION_REGISTER = 0x00;
    private const byte TEMPERATURE_BASE_REG = 0x29;
    private const byte VENDOR_ID_REGISTER = 0x58;
    private const byte FAN_TACHOMETER_DIVISOR_REGISTER = 0x0B;
    private readonly byte[] FAN_TACHOMETER_REG = 
      { 0x0d, 0x0e, 0x0f, 0x80, 0x82 };
    private readonly byte[] FAN_TACHOMETER_EXT_REG =
      { 0x18, 0x19, 0x1a, 0x81, 0x83 };
    private const byte VOLTAGE_BASE_REG = 0x20;
    private readonly byte[] FAN_PWM_CTRL_REG = { 0x15, 0x16, 0x17 };

    private bool[] restoreDefaultFanPwmControlRequired = new bool[3];       
    private byte[] initialFanPwmControl = new byte[3];

    private byte ReadByte(byte register, out bool valid) {
      Ring0.WriteIoPort(addressReg, register);
      byte value = Ring0.ReadIoPort(dataReg);
      valid = register == Ring0.ReadIoPort(addressReg);
      return value;
    }

    private bool WriteByte(byte register, byte value) {
      Ring0.WriteIoPort(addressReg, register);
      Ring0.WriteIoPort(dataReg, value);
      return register == Ring0.ReadIoPort(addressReg); 
    }

    public byte? ReadGPIO(int index) {
      if (index >= gpioCount)
        return null;

      return Ring0.ReadIoPort((ushort)(gpioAddress + index));
    }

    public void WriteGPIO(int index, byte value) {
      if (index >= gpioCount)
        return;

      Ring0.WriteIoPort((ushort)(gpioAddress + index), value);
    } 

    private void SaveDefaultFanPwmControl(int index) {
      bool valid;
      if (!restoreDefaultFanPwmControlRequired[index]) {
        initialFanPwmControl[index] = 
          ReadByte(FAN_PWM_CTRL_REG[index], out valid);
        restoreDefaultFanPwmControlRequired[index] = true;
      }
    }

    private void RestoreDefaultFanPwmControl(int index) {
      if (restoreDefaultFanPwmControlRequired[index]) {
        WriteByte(FAN_PWM_CTRL_REG[index], initialFanPwmControl[index]);
        restoreDefaultFanPwmControlRequired[index] = false;
      }
    }

    public void SetControl(int index, byte? value) {
      if (index < 0 || index >= controls.Length)
        throw new ArgumentOutOfRangeException("index");

      if (!Ring0.WaitIsaBusMutex(10))
        return;

      if (value.HasValue) {
        SaveDefaultFanPwmControl(index);

        // set output value
        WriteByte(FAN_PWM_CTRL_REG[index], (byte)(value.Value >> 1));  
      } else {
        RestoreDefaultFanPwmControl(index);
      }

      Ring0.ReleaseIsaBusMutex();
    } 

    public IT87XX(Chip chip, ushort address, ushort gpioAddress, byte version) {

      this.address = address;
      this.chip = chip;
      this.version = version;
      this.addressReg = (ushort)(address + ADDRESS_REGISTER_OFFSET);
      this.dataReg = (ushort)(address + DATA_REGISTER_OFFSET);
      this.gpioAddress = gpioAddress;

      // Check vendor id
      bool valid;
      byte vendorId = ReadByte(VENDOR_ID_REGISTER, out valid);       
      if (!valid || vendorId != ITE_VENDOR_ID)
        return;

      // Bit 0x10 of the configuration register should always be 1
      if ((ReadByte(CONFIGURATION_REGISTER, out valid) & 0x10) == 0)
        return;
      if (!valid)
        return;

      voltages = new float?[9];
      temperatures = new float?[3];
      fans = new float?[chip == Chip.IT8705F ? 3 : 5];
      controls = new float?[3];

      // IT8721F, IT8728F and IT8772E use a 12mV resultion ADC, all others 16mV
      if (chip == Chip.IT8721F || chip == Chip.IT8728F || chip == Chip.IT8771E 
        || chip == Chip.IT8772E) 
      {
        voltageGain = 0.012f;
      } else {
        voltageGain = 0.016f;        
      }

      // older IT8705F and IT8721F revisions do not have 16-bit fan counters
      if ((chip == Chip.IT8705F && version < 3) || 
          (chip == Chip.IT8712F && version < 8)) 
      {
        has16bitFanCounter = false;
      } else {
        has16bitFanCounter = true;
      }

      // Set the number of GPIO sets
      switch (chip) {
        case Chip.IT8712F:
        case Chip.IT8716F:
        case Chip.IT8718F:
        case Chip.IT8726F:
          gpioCount = 5;
          break;
        case Chip.IT8720F:
        case Chip.IT8721F:
          gpioCount = 8;
          break;
        case Chip.IT8705F: 
        case Chip.IT8728F:
        case Chip.IT8771E:
        case Chip.IT8772E:
          gpioCount = 0;
          break;
      }
    }

    public Chip Chip { get { return chip; } }
    public float?[] Voltages { get { return voltages; } }
    public float?[] Temperatures { get { return temperatures; } }
    public float?[] Fans { get { return fans; } }
    public float?[] Controls { get { return controls; } }

    public string GetReport() {
      StringBuilder r = new StringBuilder();

      r.AppendLine("LPC " + this.GetType().Name);
      r.AppendLine();
      r.Append("Chip ID: 0x"); r.AppendLine(chip.ToString("X"));
      r.Append("Chip Version: 0x"); r.AppendLine(
        version.ToString("X", CultureInfo.InvariantCulture));
      r.Append("Base Address: 0x"); r.AppendLine(
        address.ToString("X4", CultureInfo.InvariantCulture));
      r.Append("GPIO Address: 0x"); r.AppendLine(
        gpioAddress.ToString("X4", CultureInfo.InvariantCulture));
      r.AppendLine();

      if (!Ring0.WaitIsaBusMutex(100))
        return r.ToString();

      r.AppendLine("Environment Controller Registers");
      r.AppendLine();
      r.AppendLine("      00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F");
      r.AppendLine();
      for (int i = 0; i <= 0xA; i++) {
        r.Append(" "); 
        r.Append((i << 4).ToString("X2", CultureInfo.InvariantCulture)); 
        r.Append("  ");
        for (int j = 0; j <= 0xF; j++) {
          r.Append(" ");
          bool valid;
          byte value = ReadByte((byte)((i << 4) | j), out valid);
          r.Append(
            valid ? value.ToString("X2", CultureInfo.InvariantCulture) : "??");
        }
        r.AppendLine();
      }
      r.AppendLine();

      r.AppendLine("GPIO Registers");
      r.AppendLine();
      for (int i = 0; i < gpioCount; i++) {
        r.Append(" ");
        r.Append(ReadGPIO(i).Value.ToString("X2",
          CultureInfo.InvariantCulture));
      }
      r.AppendLine();
      r.AppendLine();

      Ring0.ReleaseIsaBusMutex();

      return r.ToString();
    }

    public void Update() {
      if (!Ring0.WaitIsaBusMutex(10))
        return;

      for (int i = 0; i < voltages.Length; i++) {
        bool valid;
        
        float value = 
          voltageGain * ReadByte((byte)(VOLTAGE_BASE_REG + i), out valid);   

        if (!valid)
          continue;
        if (value > 0)
          voltages[i] = value;  
        else
          voltages[i] = null;
      }

      for (int i = 0; i < temperatures.Length; i++) {
        bool valid;
        sbyte value = (sbyte)ReadByte(
          (byte)(TEMPERATURE_BASE_REG + i), out valid);
        if (!valid)
          continue;

        if (value < sbyte.MaxValue && value > 0)
          temperatures[i] = value;
        else
          temperatures[i] = null;       
      }

      if (has16bitFanCounter) {
        for (int i = 0; i < fans.Length; i++) {
          bool valid;
          int value = ReadByte(FAN_TACHOMETER_REG[i], out valid);
          if (!valid)
            continue;
          value |= ReadByte(FAN_TACHOMETER_EXT_REG[i], out valid) << 8;
          if (!valid)
            continue;

          if (value > 0x3f) {
            fans[i] = (value < 0xffff) ? 1.35e6f / (value * 2) : 0;
          } else {
            fans[i] = null;
          }
        }
      } else {
        for (int i = 0; i < fans.Length; i++) {
          bool valid;
          int value = ReadByte(FAN_TACHOMETER_REG[i], out valid);
          if (!valid)
            continue;

          int divisor = 2;
          if (i < 2) {
            int divisors = ReadByte(FAN_TACHOMETER_DIVISOR_REGISTER, out valid);
            if (!valid)
              continue;
            divisor = 1 << ((divisors >> (3 * i)) & 0x7);
          }

          if (value > 0) {
            fans[i] = (value < 0xff) ? 1.35e6f / (value * divisor) : 0;
          } else {
            fans[i] = null;
          }
        }
      }

      for (int i = 0; i < controls.Length; i++) {
        bool valid;
        byte value = ReadByte(FAN_PWM_CTRL_REG[i], out valid);
        if (!valid)
          continue;

        if ((value & 0x80) > 0) {
          // automatic operation (value can't be read)
          controls[i] = null;  
        } else {
          // software operation
          controls[i] = (float)Math.Round((value & 0x7F) * 100.0f / 0x7F);
        }
      }

      Ring0.ReleaseIsaBusMutex();
    }
  } 
}
