using System;
using System.Collections.Generic;
using System.Text;
using System.IO.Ports;
using System.Threading;
using System.Globalization;

namespace BusPirate
{
	#region Enums 
	public enum BP_PUULUP
	{
		None = 1,
		Internal3V3 = 2,
		Internal5V = 3
	}

	public enum BP_RESPONSE
	{
		OK,
		Timeout,
		Unknown,
		Disconnect,
		BadResponse,
		None
	}

	public enum BP_OUTPUT_FORMAT
	{
		Hex = 1,
		Dec = 2,
		Bin = 3,
		Raw = 4
	}

	public enum BP_SPI_SPEED
	{
		Speed_30KHz = 1,
		Speed_125KHz = 2,
		Speed_250KHz = 3,
		Speed_1MHz = 4
	}

	public enum BP_SPI_CLOCK_POLARITY
	{
		Low = 1,
		High = 2
	}

	public enum BP_SPI_CLOCK_EDGE
	{
		Idle = 1,
		Active = 2
	}

	public enum BP_SPI_INPUT_PHASE
	{
		Middle = 1,
		End = 2
	}

	public enum BP_CS_POLARITY
	{
		Positive = 1,
		Negative = 2
	}

	public enum BP_OUTPUT
	{
		OpenDrain = 1,
		Normal = 2
	}

	public enum BP_UART_SPEED
	{
		BPS_300 = 1,
		BPS_1200 = 2,
		BPS_2400 = 3,
		BPS_4800 = 4,
		BPS_9600 = 5,
		BPS_19200 = 6,
		BPS_38400 = 7,
		BPS_57600 = 8,
		BPS_115200 = 9
	}

	public enum BP_UART_PROTOCOL
	{
		Data_8_None = 1,
		Data_8_Even = 2,
		Data_8_Odd = 3,
		Data_9_None = 4
	}

	public enum BP_UART_STOP
	{
		Stop_1 = 1,
		Stop_2 = 2
	}
	public enum BP_UART_POLARITY
	{
		Idle_1 = 1,
		Idle_0 = 2
	}

	public enum BP_I2C_SPEED
	{
		I2C_5KHz = 1,
		I2C_50KHz = 2,
		I2C_100KHz = 3,
		I2C_400KHz = 4
	}

	public enum BP_WIRE_SPEED
	{
		Speed_5KHz = 1,
		Speed_50KHz = 2,
		Speed_100KHz = 3,
		Speed_400KHz = 4
	}

	public enum BP_MODE
	{
		HiZ = 1,
		WIRE1 = 2,
		UART = 3,
		I2C = 4,
		SPI = 5,
		WIRE2 = 6,
		WIRE3 = 7,
		LCD = 8,
		DIO = 9
	}

	public enum BP_PIN_TYPE
	{
		Power,
		Analog,
		Input,
		Output
	}
	#endregion

	#region Pins 
	public struct Pin
	{
		public string Name { get; internal set; }
		public BP_PIN_TYPE Type { get; internal set; }
		public double Analog;
		public bool Digital;
	}
	#endregion

	public sealed class BPPort : SerialPort, IDisposable
	{
		public delegate void WaitResponseEventHandle(object sender);
		public event WaitResponseEventHandle WaitResponse;
		public delegate void RecieveDataEventHandle(object sender, string data);
		public event RecieveDataEventHandle RecieveData;
		public delegate void SendStartedEventHandle(object sender, string data);
		public event SendStartedEventHandle SendStarted;
		public delegate void SendEndedEventHandle(object sender, string data);
		public event SendEndedEventHandle SendEnded;

		public const string BP_CMD_PROMPT = "\nHiZ>;\n1-WIRE>;\nUART>;\nI2C>;\nSPI>;\n2WIRE>;\n3WIRE>;\nLCD>;\nDIO>;\n(1)>;\n(2)>";

		private char[] C_SEMICOMMA = new char[] { ';' };
		private char[] C_TAB = new char[] { '\t' };

		private SerialDataReceivedEventHandler handler;
		private List<string> bp_lines;
		private string bp_buffer = string.Empty;
		private bool bp_processing = false;
		private List<string> bp_waitFor;
		private bool bp_waitFound;
		private char[] linefeed = new char[] { '\n' };

		public bool IsConnected { get; set; }

		private List<string> m_prompts;
		private BP_MODE m_mode;
		private object m_lock = new object();
		private object m_bufferLock = new object();
		private AutoResetEvent m_responseEvent = new AutoResetEvent(false);

		public string HardwareVersion { get; set; }
		public string FirmwareVersion { get; set; }
		public string PICVersion { get; set; }
		public Pin[] Pins = new Pin[10];
		public bool EnableRecieveData { get; set; }

		public BPPort(string portName)
			: base(portName)
		{
			BaudRate = 115200;
			Parity = Parity.None;
			StopBits = StopBits.One;
			Handshake = Handshake.None; // .RequestToSend;
			NewLine = "\r";
			IsConnected = false;

			bp_lines = new List<string>(100);
			m_prompts = new List<string>();
			foreach (string p in BP_CMD_PROMPT.Split(C_SEMICOMMA))
			{
				if (!string.IsNullOrEmpty(p))
					m_prompts.Add(p.Trim());
			}

			Pins[0].Type = BP_PIN_TYPE.Power;
			Pins[1].Type = BP_PIN_TYPE.Power;
			Pins[2].Type = BP_PIN_TYPE.Power;
			Pins[3].Type = BP_PIN_TYPE.Analog;
			Pins[4].Type = BP_PIN_TYPE.Analog;
			Pins[5].Type = BP_PIN_TYPE.Input;
			Pins[6].Type = BP_PIN_TYPE.Input;
			Pins[7].Type = BP_PIN_TYPE.Input;
			Pins[8].Type = BP_PIN_TYPE.Input;
			Pins[9].Type = BP_PIN_TYPE.Input;

			EnableRecieveData = true;
		}

		public BP_MODE Mode
		{
			get { return m_mode; }
			set
			{
				if (m_mode != value)
				{
					m_mode = value;
					switch (m_mode)
					{
						case BP_MODE.HiZ:
							ModeHiZ();
							break;
						case BP_MODE.WIRE1:
							ModeHiZ();
							break;
						case BP_MODE.LCD:
							ModeLCD();
							break;
						case BP_MODE.DIO:
							ModeLCD();
							break;
						default:
							throw new ArgumentException("Bad mode value for set");
					}
				}
			}
		}

		private string GetLine(int index)
		{
			if (index < bp_lines.Count)
				return bp_lines[index] ?? "";
			return string.Empty;
		}
		
		public BP_RESPONSE ModeHiZ()
		{
			m_mode = BP_MODE.HiZ;
			return Execute("m1");
		}

		public BP_RESPONSE ModeWIRE1()
		{
			m_mode = BP_MODE.WIRE1;
			return Execute("m2");
		}

		public BP_RESPONSE ModeUART(BP_UART_SPEED speed, BP_UART_PROTOCOL protocol, BP_UART_STOP stop, BP_UART_POLARITY polarity, BP_OUTPUT output)
		{
			m_mode = BP_MODE.UART;
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected &&
				(response = SendWait("m3")) == BP_RESPONSE.OK &&
				(response = SendWait((int)speed)) == BP_RESPONSE.OK &&
				(response = SendWait((int)protocol)) == BP_RESPONSE.OK &&
				(response = SendWait((int)stop)) == BP_RESPONSE.OK &&
				(response = SendWait((int)polarity)) == BP_RESPONSE.OK
				)
				response = SendWait(((int)output));
			return response;
		}

		public BP_RESPONSE ModeI2C(BP_I2C_SPEED speed)
		{
			m_mode = BP_MODE.I2C;
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected &&
				(response = SendWait("m4")) == BP_RESPONSE.OK
				)
				response = SendWait(((int)speed));
			return response;
		}

		public BP_RESPONSE ModeSPI(BP_SPI_SPEED speed, BP_SPI_CLOCK_POLARITY polarity, BP_SPI_CLOCK_EDGE edge, BP_SPI_INPUT_PHASE phase, BP_CS_POLARITY cs, BP_OUTPUT output)
		{
			m_mode = BP_MODE.SPI;
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected &&
				(response = SendWait("m5")) == BP_RESPONSE.OK &&
				(response = SendWait((int)speed)) == BP_RESPONSE.OK &&
				(response = SendWait((int)polarity)) == BP_RESPONSE.OK &&
				(response = SendWait((int)edge)) == BP_RESPONSE.OK &&
				(response = SendWait((int)phase)) == BP_RESPONSE.OK &&
				(response = SendWait((int)cs)) == BP_RESPONSE.OK
				)
				response = SendWait((int)output);
			return response;
		}

		public BP_RESPONSE ModeWIRE2(BP_WIRE_SPEED speed, BP_OUTPUT output)
		{
			m_mode = BP_MODE.WIRE2;
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected &&
				(response = SendWait("m6")) == BP_RESPONSE.OK &&
				(response = SendWait((int)speed)) == BP_RESPONSE.OK
				)
				response = SendWait((int)output);
			return response;
		}

		public BP_RESPONSE ModeWIRE3(BP_WIRE_SPEED speed, BP_CS_POLARITY cs, BP_OUTPUT output)
		{
			m_mode = BP_MODE.WIRE3;
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected &&
				(response = SendWait("m7")) == BP_RESPONSE.OK &&
				(response = SendWait((int)speed)) == BP_RESPONSE.OK &&
				(response = SendWait((int)cs)) == BP_RESPONSE.OK
				)
				response = SendWait((int)output);
			return response;
		}

		public BP_RESPONSE ModeLCD()
		{
			m_mode = BP_MODE.LCD;
			return Execute("m8");
		}
		public BP_RESPONSE ModeDIO()
		{
			m_mode = BP_MODE.DIO;
			return Execute("m9");
		}

		public BP_RESPONSE AuxLow()
		{
			return Execute("a");
		}

		public BP_RESPONSE AuxHigh()
		{
			return Execute("A");

		}

		public BP_RESPONSE AuxRead()
		{
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected && (response = SendWait("@")) == BP_RESPONSE.OK)
			{
				response = BP_RESPONSE.BadResponse;
				if (bp_lines.Count >= 3)
				{
					string value = GetLine(1);
					if (value.EndsWith("0"))
					{
						Pins[5].Digital = false;
						response = BP_RESPONSE.OK;
					}
					else if (value.EndsWith("1"))
					{
						Pins[5].Digital = true;
						response = BP_RESPONSE.OK;
					}
				}
			}
			return response;
		}

		internal BP_RESPONSE Delay_mS(int delay)
		{
			return Execute(string.Format("%:{0}", delay));
		}

		internal BP_RESPONSE Delay_uS(int delay)
		{
			return Execute(string.Format("&:{0}", delay));
		}

		internal BP_RESPONSE PowerOff()
		{
			return Execute("w");
		}

		internal BP_RESPONSE PowerOn()
		{
			return Execute("W");
		}

		internal BP_RESPONSE Voltage()
		{
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected && (response = SendWait("v")) == BP_RESPONSE.OK)
			{
				int pinstatesIdx = bp_lines.FindIndex(l => l.StartsWith("Pinstates:", StringComparison.OrdinalIgnoreCase));
				int startSearch = (pinstatesIdx >= 0) ? pinstatesIdx + 1 : 0;

				string nameLine = null, modeLine = null, voltLine = null;
				for (int i = startSearch; i < bp_lines.Count; i++)
				{
					string line = bp_lines[i];
					if (string.IsNullOrEmpty(line)) continue;

					string[] parts = line.Split(new char[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length > 0 && (parts[0] == "P" || parts[0] == "I" || parts[0] == "O") && parts.Length >= 3)
					{
						modeLine = line;
					}
					else if (parts.Length > 0 && (parts[0] == "GND" || parts[0].EndsWith("V") || parts[0] == "H" || parts[0] == "L"))
					{
						voltLine = line;
					}
					else if (nameLine == null && (line.Contains("ADC") || line.Contains("AUX") || line.Contains("CLK") || line.Contains("MOSI") || line.Contains("CS") || line.Contains("MISO") || line.Contains("3.3V") || line.Contains("5.0V") || line.Contains("VPULL")))
					{
						nameLine = line;
					}
				}

				if (modeLine != null) ParsePinMode(modeLine);
				if (voltLine != null) ParseVoltage(voltLine);
				if (nameLine != null) ParsePinName(nameLine);

				response = BP_RESPONSE.OK;
			}
			return response;
		}

		private BP_RESPONSE ParsePinMode(string modes)
		{
			if (!string.IsNullOrEmpty(modes))
			{
				int pin = 0;
				string[] tokens = modes.Split(new char[] { '\t' }, StringSplitOptions.None);
				if (tokens.Length <= 1)
					tokens = modes.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

				foreach (string mode in tokens)
				{
					if (pin >= Pins.Length)
						break;

					string mStr = mode.Trim();
					if (string.IsNullOrEmpty(mStr)) continue;

					BP_PIN_TYPE m;
					if (mStr == "P")
						m = BP_PIN_TYPE.Power;
					else if (mStr == "I" && (pin == 3 || pin == 4))
						m = BP_PIN_TYPE.Analog;
					else if (mStr == "I")
						m = BP_PIN_TYPE.Input;
					else if (mStr == "O")
						m = BP_PIN_TYPE.Output;
					else
						continue;

					Pins[pin].Type = m;
					pin++;
				}
				return BP_RESPONSE.OK;
			}
			return BP_RESPONSE.BadResponse;
		}

		private BP_RESPONSE ParsePinName(string names)
		{
			if (!string.IsNullOrEmpty(names))
			{
				int pin = 0;
				string[] tokens = names.Split(new char[] { '\t' }, StringSplitOptions.None);
				if (tokens.Length <= 1)
					tokens = names.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

				foreach (string name in tokens)
				{
					if (pin >= Pins.Length)
						break;

					string nStr = name.Trim();
					if (string.IsNullOrEmpty(nStr)) continue;

					Pins[pin].Name = nStr;
					pin++;
				}
				return BP_RESPONSE.OK;
			}
			return BP_RESPONSE.BadResponse;
		}

		private BP_RESPONSE ParseVoltage(string values)
		{
			if (!string.IsNullOrEmpty(values))
			{
				int pin = 0;
				double val;
				string[] tokens = values.Split(new char[] { '\t' }, StringSplitOptions.None);
				if (tokens.Length <= 1)
					tokens = values.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

				foreach (string value in tokens)
				{
					if (pin >= Pins.Length)
						break;

					string vStr = value.Trim();
					if (string.IsNullOrEmpty(vStr)) continue;

					if (vStr == "GND")
						Pins[pin].Analog = 0.0;
					else if (vStr == "H")
						Pins[pin].Digital = true;
					else if (vStr == "L")
						Pins[pin].Digital = false;
					else if (vStr.EndsWith("V", StringComparison.OrdinalIgnoreCase))
					{
						if (double.TryParse(vStr.Substring(0, vStr.Length - 1), System.Globalization.NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out val))
							Pins[pin].Analog = val;
					}
					pin++;
				}
				return BP_RESPONSE.OK;
			}
			return BP_RESPONSE.BadResponse;
		}

		public BP_RESPONSE SetAUX2AUX()
		{
			return Execute("c");
		}
		public BP_RESPONSE SetAUX2CS()
		{
			return Execute("C");
		}

		public BP_RESPONSE Reset()
		{
			return Execute("#y");
		}
		public BP_RESPONSE Bootloader()
		{
			return Execute("$y");
		}

		public BP_RESPONSE OutputHex()
		{
			return Execute("o1");
		}
		public BP_RESPONSE OutputDec()
		{
			return Execute("o2");
		}
		public BP_RESPONSE OutputBin()
		{
			return Execute("o3");
		}
		public BP_RESPONSE OutputRaw()
		{
			return Execute("o4");
		}
		public BP_RESPONSE SetMSB()
		{
			return Execute("l");
		}
		public BP_RESPONSE SetLSB()
		{
			return Execute("L");
		}
		public BP_RESPONSE DisablePWM()
		{
			return Execute("g");
		}

		public BP_RESPONSE Pullup(BP_PUULUP pullup)
		{
			return Execute(string.Format("e{0}", (int)pullup));
		}
		public BP_RESPONSE PullupVPU()
		{
			return Execute("e1");
		}
		public BP_RESPONSE Pullup3V3()
		{
			return Execute("e2");
		}
		public BP_RESPONSE Pullup5V()
		{
			return Execute("e3");
		}
		public BP_RESPONSE PullupOn()
		{
			return Execute("P");
		}
		public BP_RESPONSE PullupOff()
		{
			return Execute("p");
		}

		public BP_RESPONSE Execute(string cmd)
		{
			return IsConnected ? SendWait(cmd) : BP_RESPONSE.Disconnect;
		}

		internal BP_RESPONSE OutputFormat(BP_OUTPUT_FORMAT format)
		{
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected)
				if ((response = SendWait("o")) == BP_RESPONSE.OK)
					response = SendWait(((int)format));
			return response;
		}

		public BP_RESPONSE Info()
		{
			BP_RESPONSE response = BP_RESPONSE.Disconnect;
			if (IsConnected && (response = SendWait("i")) == BP_RESPONSE.OK)
			{
				response = BP_RESPONSE.BadResponse;
				if (bp_lines.Count >= 2)
				{
					HardwareVersion = bp_lines.Find(l => l.StartsWith("Bus Pirate", StringComparison.OrdinalIgnoreCase)) ?? GetLine(1);
					FirmwareVersion = bp_lines.Find(l => l.StartsWith("Firmware", StringComparison.OrdinalIgnoreCase)) ?? GetLine(2);
					PICVersion = bp_lines.Find(l => l.StartsWith("DEVID", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Bootloader", StringComparison.OrdinalIgnoreCase)) ?? GetLine(3);

					if (string.IsNullOrEmpty(HardwareVersion)) HardwareVersion = "Bus Pirate";
					if (string.IsNullOrEmpty(FirmwareVersion)) FirmwareVersion = "v6.1";
					if (string.IsNullOrEmpty(PICVersion)) PICVersion = "PIC24";

					if (bp_lines.Count > 0)
					{
						string mode = bp_lines[bp_lines.Count - 1].Trim();
						int idx = m_prompts.FindIndex(p => mode.Equals(p, StringComparison.OrdinalIgnoreCase) || mode.EndsWith(p, StringComparison.OrdinalIgnoreCase));
						if (idx >= 0)
						{
							m_mode = (BP_MODE)(idx + 1);
						}
						else
						{
							m_mode = BP_MODE.HiZ;
						}
					}

					int pinstatesIdx = bp_lines.FindIndex(l => l.StartsWith("Pinstates:", StringComparison.OrdinalIgnoreCase));
					if (pinstatesIdx >= 0)
					{
						string nameLine = null, modeLine = null, voltLine = null;
						for (int i = pinstatesIdx + 1; i < bp_lines.Count; i++)
						{
							string line = bp_lines[i];
							if (string.IsNullOrEmpty(line)) continue;

							string[] parts = line.Split(new char[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
							if (parts.Length > 0 && (parts[0] == "P" || parts[0] == "I" || parts[0] == "O") && parts.Length >= 3)
							{
								modeLine = line;
							}
							else if (parts.Length > 0 && (parts[0] == "GND" || parts[0].EndsWith("V") || parts[0] == "H" || parts[0] == "L"))
							{
								voltLine = line;
							}
							else if (nameLine == null && (line.Contains("ADC") || line.Contains("AUX") || line.Contains("CLK") || line.Contains("MOSI") || line.Contains("CS") || line.Contains("MISO") || line.Contains("3.3V") || line.Contains("5.0V") || line.Contains("VPULL")))
							{
								nameLine = line;
							}
						}

						if (modeLine != null) ParsePinMode(modeLine);
						if (voltLine != null) ParseVoltage(voltLine);
						if (nameLine != null) ParsePinName(nameLine);
					}

					response = BP_RESPONSE.OK;
				}
			}
			return response;
		}

		public BP_RESPONSE Connect()
		{
			BP_RESPONSE response = BP_RESPONSE.OK;
			if (!IsConnected)
			{
				Open();
				try
				{
					DiscardInBuffer();
					DiscardOutBuffer();
					Write("\r");
					Thread.Sleep(50);
					DiscardInBuffer();
					DiscardOutBuffer();
				}
				catch { }

				if (handler == null)
				{
					handler = new SerialDataReceivedEventHandler(BPPort_DataReceived);
					DataReceived += handler;
				}

				if ((response = SendWait("", m_prompts, 5, 1000)) == BP_RESPONSE.OK)
				{
					IsConnected = true;
					response = Info();
				}
				else
					Close();
			}
			return response;
		}

		public void Disconnect()
		{
			IsConnected = false;
			if (handler != null)
			{
				DataReceived -= handler;
				handler = null;
			}
			if (IsOpen)
				Close();
		}

		private bool disposed = true;
		protected override void Dispose(bool disposing)
		{
			if (!disposing && disposed)
			{
				disposed = false;
				Disconnect();
			}
			base.Dispose(disposing);
		}

		public BP_RESPONSE Send(char c)
		{
			try
			{
				Write(c.ToString());
			}
			catch { }
			return BP_RESPONSE.OK;
		}

		/// <summary>
		/// Send command and wait for command prompt
		/// </summary>
		/// <param name="command"></param>
		/// <returns></returns>
		public BP_RESPONSE SendWait(string command)
		{
			return SendWait(command, m_prompts, 1, 1000);
		}
		public BP_RESPONSE SendWait(int command)
		{
			return SendWait(command.ToString(), m_prompts, 1, 1000);
		}

		public BP_RESPONSE SendWait(string command, List<string> waitFor, int retry, uint timeout)
		{
			BP_RESPONSE response = BP_RESPONSE.Timeout;
			lock (m_lock)
			{
				try
				{
					if (SendStarted != null)
						SendStarted(this, command);

					bp_waitFor = waitFor;

					while (retry-- > 0)
					{
						PrepareSend();
						bp_processing = true;
						WriteTimeout = 5000;
						WriteLine(command);

						if (m_responseEvent.WaitOne((int)timeout))
						{
							response = BP_RESPONSE.OK;
							break;
						}
					}
				}
				catch (TimeoutException)
				{
					response = BP_RESPONSE.Timeout;
				}
				catch
				{
					response = BP_RESPONSE.Unknown;
				}
				bp_processing = false;
				if (SendEnded != null)
					SendEnded(this, command);
			}
			return response;
		}

		private void PrepareSend()
		{
			lock (m_bufferLock)
			{
				bp_processing = false;
				bp_waitFound = false;
				bp_buffer = string.Empty;
				bp_lines.Clear();
				m_responseEvent.Reset();
			}
		}

		void BPPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
		{
			BPPort port = sender as BPPort;
			if (port != null && port.BytesToRead > 0 && e.EventType == SerialData.Chars)
			{
				string data = port.ReadExisting();
				if (bp_processing)
				{
					lock (m_bufferLock)
					{
						bp_buffer += data;
						int lineEnd;
						while ((lineEnd = bp_buffer.IndexOfAny(new char[] { '\r', '\n' })) >= 0)
						{
							string line = bp_buffer.Substring(0, lineEnd).Trim();
							int nextChar = lineEnd;
							while (nextChar < bp_buffer.Length && (bp_buffer[nextChar] == '\r' || bp_buffer[nextChar] == '\n'))
							{
								nextChar++;
							}
							bp_buffer = bp_buffer.Substring(nextChar);

							if (!string.IsNullOrEmpty(line))
							{
								bp_lines.Add(line);
							}
						}

						if (bp_waitFor != null && !string.IsNullOrEmpty(bp_buffer))
						{
							string trimmedBuf = bp_buffer.Trim();
							foreach (string prompt in bp_waitFor)
							{
								string p = prompt.Trim();
								if (!string.IsNullOrEmpty(p) && (trimmedBuf.EndsWith(p, StringComparison.OrdinalIgnoreCase) || trimmedBuf.Equals(p, StringComparison.OrdinalIgnoreCase)))
								{
									bp_lines.Add(trimmedBuf);
									bp_buffer = string.Empty;
									bp_waitFound = true;
									m_responseEvent.Set();
									break;
								}
							}
						}
					}
				}
				else
				{
					lock (m_bufferLock)
					{
						bp_buffer = string.Empty;
					}
				}

				if (EnableRecieveData && RecieveData != null)
				{
					try
					{
						RecieveData(this, data);
					}
					catch { }
				}
			}
		}

		private void ProcessResponse(string cmd)
		{
			if (cmd != null)
			{
				cmd = cmd.TrimStart(linefeed).Trim();
				if (!string.IsNullOrEmpty(cmd))
				{
					bp_lines.Add(cmd);
				}
			}
		}
	}
}