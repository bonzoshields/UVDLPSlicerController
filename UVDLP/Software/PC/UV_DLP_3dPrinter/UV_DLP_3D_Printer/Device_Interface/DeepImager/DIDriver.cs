﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Ports;
using System.Timers;

namespace UV_DLP_3D_Printer.Drivers
{
    /// <summary>
    /// This class is to drive the Deep Imager 5 printer from
    /// Elite Image Works
    /// The basic concept is to translate the GCode generated by this application
    /// into the binary protocol used by the Elite Image Works Deep Imager 5 printer
    /// This filter/driver layers allows for 
    /// re-interpreting the gcode commands as they are sent from the build manager / machine controls
    /// and placing them into the X-byte binary protocol used by DI5
    /// 
    /// To start, this will only implement the G1 and G28 commands
    /// The plan is to implement all machines spcial commands that do not correspond to Gcode or M codes
    /// as special MCodes
    /// </summary>
    public class DIDriver : GenericDriver
    {
        Timer m_reqtimer;
        private static double s_interval = 250; // 1/4 second
        public DIDriver() 
        {
            m_reqtimer = new Timer();
            m_reqtimer.Interval = s_interval;
            m_reqtimer.Elapsed += new ElapsedEventHandler(m_reqtimer_Elapsed);
            UVDLPApp.Instance().m_deviceinterface.AlwaysReady = true; // don't looks for gcode responses, always assume we're ready for the next command.
        }

        /// <summary>
        /// Set up a time to request system information at every interval
        /// </summary>
        private void StartRequestTimer() 
        {
            m_reqtimer.Start();
        }
        /// <summary>
        /// override the base class implementation of the connect
        /// so we can start the timer
        /// </summary>
        /// <returns></returns>
        public override bool Connect() 
        {
            bool ret = false;
            try
            {
                ret = base.Connect();
                StartRequestTimer();
                return ret;
            }
            catch (Exception ex) 
            {
                DebugLogger.Instance().LogRecord(ex.Message);
                return ret;                
            }
        }
        /// <summary>
        /// Override the base class implementation of the disconnect
        /// in order to stop the request status timer
        /// </summary>
        /// <returns></returns>
        public override bool Disconnect()
        {
            try
            {
                bool ret = base.Disconnect();
                StopRequestTimer();
                return ret;
            }
            catch (Exception ex)
            {
                DebugLogger.Instance().LogRecord(ex.Message);
                return false;
            }

        }
        void m_reqtimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                // send a message to request info
                byte[] cmdreq = GenerateCommand();
                cmdreq[1] = (byte)'S'; // system command
                cmdreq[2] = (byte)'R'; // request status
                Checksum(ref cmdreq); // add the checksum
                Write(cmdreq, 8); // send the request
            }
            catch (Exception ex) 
            {
                DebugLogger.Instance().LogError(ex);
            }
        }

        private void StopRequestTimer() 
        {
            m_reqtimer.Stop();
        }
        /// <summary>
        /// This function adds the checksum byte to the end of the command being sent
        /// </summary>
        /// <param name="data"></param>
        private void Checksum(ref byte []data)
        {
            byte cs = 0;
            for(int c = 0; c< 7; c++)
            {
                cs += data[c];
            }
            data[7] = cs;
        }

        /// <summary>
        /// This generates an empty command
        /// </summary>
        /// <returns></returns>
        private byte[] GenerateCommand() 
        {
            byte[] retval = new byte[8];
            for (int c = 0; c < 8; c++) 
            {
                retval[c] = 0;
            }
            retval[0] = (byte)'@'; // set the first character to be the @ symbol
            return retval;    
        }
        /// <summary>
        /// This function starts looking at the 2 character in the line (index 1)
        /// and will read characters until the whitespace
        /// and return the g/m code
        /// </summary>
        /// <param name="?"></param>
        /// <returns></returns>
        private int GetGMCode(string line)
        {
            try
            {
                int idx = 1;
                string val = "";
                string ss = line.Substring(idx, 1);
                while (ss != " " && ss != "\r" && idx < line.Length) 
                {
                    ss = line.Substring(idx++, 1);
                    val += ss;
                }
                int retval = int.Parse(val);
                return retval;
            }catch(Exception ex)
            {
                DebugLogger.Instance().LogError(ex);
            }
            return -1;
        }
        
        private double GetGCodeValDouble(string line, char var) 
        {
            try
            {
                // scan the string, looking for the specified var
                // starting at the next position, start reading characters
                // until a space occurs or we reach the end of the line
                double val = 0;
                int idx = line.IndexOf(var);
                if (idx != -1)
                {
                    // found the character
                    //look for the next space or end of line
                    string sval = "";
                    string ss = line.Substring(idx++, 1);
                    while (ss != " " && ss != "\r" && idx < line.Length)
                    {
                        ss = line.Substring(idx++, 1);
                        sval += ss;
                    }
                    val = double.Parse(sval.Trim());
                }
                return val;
            }
            catch (Exception ex) 
            {
                DebugLogger.Instance().LogError(ex);
                return 0.0;
            }
        }
        /// <summary>
        /// This function needs to convert from mm to .0005 thousandths of an inch
        /// </summary>
        /// <param name="mm"></param>
        /// <returns></returns>
        private byte CalcZSteps(double mm) 
        {
            
            double mm2inch = 0.0393701;
            double conv = mm * mm2inch;// convert mm to inch
            double val = conv / .0005;//divide inches by .0005
            byte retval = (byte)Math.Round(val); // hopefully rounding won't be necessary if we've choosen slice height correctly
            return retval;
            
        }
        /// <summary>
        /// This interprets the gcode/mcode
        /// generates a command, and sends the data to the port
        /// it returns number of bytes written
        /// </summary>
        /// <param name="line"></param>
        private int InterpretGCode(string line) 
        {
            try
            {
                int retval = 0;
                string ln = line.Trim(); // trim the line to remove any leading / trailing whitespace
                ln = ln.ToUpper(); // convert to all upper case for easier processing
                int code = -1;
                byte[] cmd;
                if (ln.StartsWith("G"))
                {
                    code = GetGMCode(line);
                    switch (code)
                    {
                        case -1:// error getting g/mcode
                            DebugLogger.Instance().LogError("Error getting G/M code: " + line);
                            break;
                        case 1: // G1 movement command - 
                            //on this printer, this is used for decrementing the position during build
                            cmd = GenerateCommand(); // generate the command
                            cmd[1] = (byte)'Z'; // indicate a Z Movement command
                            double zval = GetGCodeValDouble(line, 'Z');
                            byte steps = CalcZSteps(zval); // this is in .005 thousandths of an inch per step
                            cmd[2] = steps; // number of steps
                            cmd[3] = 0; // no fill for now
                            cmd[4] = 0; // no fill for now
                            cmd[5] = 0; // no fill for now

                            Checksum(ref cmd); // add the checksum
                            retval = Write(cmd, 8); // send the command
                            break;
                        case 28: // G28 Homing command
                            cmd = GenerateCommand(); // generate the command
                            cmd[1] = (byte)'S'; // indicate a System command
                            cmd[2] = (byte)'H'; // Homing command
                            Checksum(ref cmd); // add the checksum
                            retval = Write(cmd, 8); // send the command
                            break;
                    }
                }
                else if (ln.StartsWith("M"))
                {
                    code = GetGMCode(line);
                    switch (code)
                    {
                        case -1:// error getting g/mcode
                            DebugLogger.Instance().LogError("Error getting G/M code: " + line);
                            break;
                        case 600: // M600 is begin print
                            // get the offset from the line
                            cmd = GenerateCommand(); // generate the command
                            cmd[1] = (byte)'S'; // indicate a System command
                            cmd[2] = (byte)'P'; // indicate a print command
                            cmd[3] = 10; // print offset in steps - hardcoded for now.
                            Checksum(ref cmd); // add the checksum
                            retval = Write(cmd, 8); // send the command
                            break;
                        case 601: // M601 is Standby mode
                            cmd = GenerateCommand(); // generate the command
                            cmd[1] = (byte)'S'; // indicate a System command
                            cmd[2] = (byte)'S'; // indicate a print command
                            Checksum(ref cmd); // add the checksum
                            retval = Write(cmd, 8); // send the command
                            break;
                        case 602: // raw passthrough, we can use this for initializing the display resolution & testing - assumes checksum is correct
                            //get from position 5 to EOL
                            string ptcmd = line.Substring(5);
                            ptcmd = ptcmd.Trim();
                            //remove any spaces
                            ptcmd = ptcmd.Replace(" ", string.Empty);                            
                            //convert from hex string to byte array
                            byte []ptraw = Utility.HexStringToByteArray(ptcmd);
                            //write that byte array directly
                            retval = Write(ptraw, ptraw.Length);
                            break;

                    }
                }
                return retval;
            }
            catch (Exception ex) 
            {
                DebugLogger.Instance().LogError(ex);
                return 0; // error writing / decoding
            }
        }
        /// <summary>
        /// We're overriding the read here
        /// We're going to need to listen to the system status messages sent back from the printer.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void m_serialport_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            int read = m_serialport.Read(m_buffer, 0, m_serialport.BytesToRead);
            byte[] data = new byte[read];
            for (int c = 0; c < read; c++)
            {
                data[c] = m_buffer[c];
            }
            Log(data, read);
            RaiseDataReceivedEvent(this, data, read);
            // we're also going to have to raise an event to the deviceinterface indicating that we're 
            // ready for the next command, because this is different than the standard
            // gcode implementation where the device interface looks for a 'ok',
            //we'll probably have to also raise a signal to the deviceinterface NOT to look for the ok
            // so it doen't keep adding up buffers.
        }

        public override int Write(String line) 
        {
            try
            {
                int sent = 0;
                string tosend = RemoveComment(line);
                lock (_locker) // ensure synchronization
                {
                    line = RemoveComment(line);
                    if (line.Trim().Length > 0)
                    {
                        Log(line);
                        sent = InterpretGCode(line);
                        // interpret the gcode line,
                        // generate the coomand
                        //send the command
                        //m_serialport.Write(line);
                    }
                    return sent;
                }
            }
            catch (Exception ex) 
            {
                DebugLogger.Instance().LogError(ex);
                return 0;
            }
        }
    }
}
