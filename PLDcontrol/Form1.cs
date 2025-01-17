﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AForge.Video.DirectShow;
using AForge.Video;
using System.Globalization;
using System.Drawing.Imaging;
using System.Threading;

namespace PLDcontrol
{
    /// <summary>
    /// Main form of the PLDControl software
    /// Communicates with laser and microcontroller via serial interface, and sends commands 
    /// and receives data. 
    /// 
    /// Lately I have been using python, and therefore I am sometimes using EAFP (Easier to ask forgiveness than permission) and many try-catch clauses.
    /// While this may not be standard way to write C#, I do not care :)
    /// </summary>
    public partial class Form1 : Form
    {

        /* 
         * Attributes 
         */
        delegate void SetTextCallback(string text);
        private Stopwatch stopwatch = new Stopwatch();
        private System.Windows.Forms.Timer laserTimer;


        // Logfile with .aki extension, the best among all extensions
        private readonly string LOGFILE_NAME= @"pldcontrol_log_file.aki";
        private string filename;
        // Asyncrohonous reading and writing logfile 
        public dataTransfer fileLock;
        private const int ASYNC_DELAY = 5;

        // PLD state values
        private double chamberTemperature = 0;
        private double N2Flow = 0;
        private double ArFlow = 0;
        private int motorPosition = 0;
        private bool hold = false;

        // Webcam attributes
        private VideoCaptureDevice videoSource;
        private FilterInfoCollection videoDevices;
        Bitmap chamberImage;

        // Serial communication attributes (Arduino)
        private SerialPort port;
        private String[] ports;
        private String currentPort;
        private const int BAUD_RATE = 9600; // Serial communication baud rate

        // Serial communication attributes (Laser)
        private SerialPort laserPort;
        private const int LASER_BAUD_RATE = 19200;
        // Buffer for reading laser messages
        private string buffer;
        // Laser status attributes
        private bool LaserOn = false;
        private const string laserPortName = "COM1";
        private int eoDelayMa = 0; // Electro-optics delay in max output mode
        private int eoDelayAd = 0; // Electro-optics delay in adjust mode
        private double laserTemp = 0; // Cooling water temperature
        private string laserMode = "OFF"; // Laser output mode
        private string laserStatus = ""; // laser status
        private int packPulses = 0;

        // Bool to indicate if this is initialization round
        private bool startup = true;

        /// <summary>
        /// Constructor of the class
        /// </summary>
        public Form1()
        {
            // Start timer to enable form after loading controls
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 3000;
            timer.Start();
            timer.Tick += delegate { Opacity = 100; };

            // Make logfile folder to myDocuments
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            path = Path.Combine(path,"\\Ruhtinas Software\\PLDControl\\Data\\");

            // Check if folder exists, if not make the folder
            DirectoryInfo di = new DirectoryInfo(path);
            if (!di.Exists)
            {
                di.Create();
            }
            // Create path to logfile
            filename = Path.Combine(path, LOGFILE_NAME);

            InitializeComponent();
            ClearLogFile(); // Clears previous logfile
            InitializeForm();

            // Create fillock object to pass filename and file lock status to ConsoleWindow
            fileLock = new dataTransfer();
            fileLock.locking = false;
            fileLock.filepath = path;

            pictureBox2.SizeMode = PictureBoxSizeMode.Zoom;

            // Create timer to update stopwatch label
            System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
            updateTimer.Interval = 10; // update time every 10 milliseconds
            updateTimer.Start();
            updateTimer.Tick += delegate {
                TimeSpan ts = stopwatch.Elapsed; // Bind label text to stopwatch value
                string elapsedTime = String.Format("{0:00}:{1:00}:{2:00}.{3:00}",ts.Hours, ts.Minutes, ts.Seconds,ts.Milliseconds / 10);
                SetTimeText(elapsedTime);
            };

            // Starting camera capture
            webcam();

            // Setting up serial communications
            DefineSerial();
            DefineLaserSerial();

            // Initialization of laser serial messaging buffer
            buffer = "";

            // uncheck offradiobutton
            offradioButton.Checked = true;

            // Startup finished
            startup = false;
        }

        /// <summary>
        /// Initializes main form
        /// </summary>
        private void InitializeForm()
        {
            // Trackbar modifications and event handler setup
            initializeTrackBars();
            sweepButton.ForeColor = Color.Gray;

            // Disabling motor sweeping by default
            rangeNumeric.Enabled = false;
            midPointNumeric.Enabled = false;

            // Disabling laser timer by default
            minNumericControl.Enabled = false;
            secNumericControl.Enabled = false;

            //Initialize dropDownStyle for serial port combobox
            toolStripComboBox1.DropDownStyle = ComboBoxStyle.DropDownList;

            // Extracting port names
            ports = SerialPort.GetPortNames();

            // initializing currentPort variable
            currentPort = "NO USB DEVICES";

            // If there is active serial ports detected
            if (ports.Length != 0)
            {
                foreach (string p in ports)
                {
                    // Add ports to port combobox
                    toolStripComboBox1.Items.Add(p);
                }
                try
                {
                    //try to select COM4 for arduino
                    toolStripComboBox1.SelectedIndex = toolStripComboBox1.FindStringExact("COM4");
                }
                catch(System.IO.IOException ex)
                {
                    // Catch exception, there is no device in combobox 4
                    toolStripComboBox1.SelectedIndex = 0;
                    writeLogFile("COM4 port empty");
                    MessageBox.Show("No device in COM4 port: select port from menu", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                }
                // Set currentPort variable to be one selected in port combobox
                currentPort = (string)toolStripComboBox1.SelectedItem;
            }
            // Creating timer for laser start7stop control
            laserTimer = new System.Windows.Forms.Timer();
        }

        /// <summary>
        /// Updating port list if menuitem is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void arduinoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ports = SerialPort.GetPortNames();
            if (ports.Length != 0)
            {
                // Adding ports to comboBox
                foreach (string p in ports)
                {
                    toolStripComboBox1.Items.Add(p);
                }
                // Selecting ports
                try {
                    //try to select COM4 for arduino and COM1 for Laser
                    toolStripComboBox1.SelectedIndex = toolStripComboBox1.FindStringExact("COM4");
                }
                catch (System.IO.IOException ex) {
                    System.Console.WriteLine(ex.Message);
                    toolStripComboBox1.SelectedIndex = 0;
                }
                currentPort = (string)toolStripComboBox1.SelectedItem;
            }
        }


        /// <summary>
        /// Initializing trackbars for better event handling than default
        /// </summary>
        private void initializeTrackBars()
        {
            /// Following is to make gas flow trackbars to send values only
            /// when mouse is clicked or released
            bool clicked_track1 = false;
            bool clicked_track2 = false;
            trackBar1.Scroll += (s, e) =>
            {
                if (clicked_track1)
                    return;
                sendGasFlow(s, e);
                N2FlowNumericControl.Value= (decimal)(10.0 * (trackBar1.Value * 1.0 / trackBar1.Maximum));
            };
            trackBar1.MouseDown += (s, e) =>
            {
                clicked_track1 = true;
            };
            trackBar1.MouseUp += (s,
                                    e) =>
            {
                if (!clicked_track1)
                    return;

                clicked_track1 = false;
                sendGasFlow(s, e);
            };

            trackBar2.Scroll += (s, e) =>
            {
                if (clicked_track2)
                    return;
                sendGasFlow(s, e);
                N2FlowNumericControl.Value = (decimal)(10.0 * (trackBar2.Value * 1.0 / trackBar2.Maximum));
            };
            trackBar2.MouseDown += (s, e) =>
            {
                clicked_track2 = true;
            };
            trackBar2.MouseUp += (s,
                                    e) =>
            {
                if (!clicked_track2)
                    return;

                clicked_track2 = false;
                sendGasFlow(s, e);
            };

            trackBar1.Maximum = 4096; //put here resolution of the gas controller 1
            trackBar1.Minimum = 0;
            trackBar1.TickFrequency = 4096 / 10;


            trackBar2.Maximum = 4096; //put here resolution of the gas controller 2
            trackBar2.Minimum = 0;
            trackBar2.TickFrequency = 4096 / 10;
        }

        /// <summary>
        /// Clears previous logfile
        /// </summary>
        private void ClearLogFile()
        {
            try
            {
                FileStream fileStream = File.Open(filename, FileMode.Open);
                fileStream.SetLength(0);
                fileStream.Close(); // This flushes the content, too.
            }
            catch (System.IO.FileNotFoundException)
            {
                MessageBox.Show("Logfile not found");//No file to clear
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            Opacity = 0;
            //this.WindowState = FormWindowState.Maximized; //Starts with maximized window
        }

        /// <summary>
        /// Initializing serial port for arduino
        /// </summary>
        private void DefineSerial()
        {
            try
            {
                if (port != null && port.IsOpen) { port.Close(); };
                port = null;

                /// Setting up arduino serial communication
                if (ports.Length != 0)
                {
                    currentPort = (string)toolStripComboBox1.SelectedItem;
                    port = new SerialPort((string)toolStripComboBox1.SelectedItem, BAUD_RATE);
                    port.RtsEnable = false; //needed if microcontroller is arduino leonardo
                    port.DtrEnable = true; //for leonardo

                    // open the port
                    port.Open();
                    if (currentPort != null) currentPortLabel.Text = currentPort;
                    port.DataReceived += new SerialDataReceivedEventHandler(ArduinoCommunicationHandler);
                }
            }
            catch(Exception ex)
            {
                // Catch general exception, not very good practice but who cares
                writeLogFile(ex.Message);
            }

        }

        /// <summary>
        ///  Establishes serial communication to laser
        /// </summary>
        private void DefineLaserSerial()
        {
            try
            {
                // Making new serial port object with 
                // settings suitable communication with Ekspla NL301 laser
                laserPort = new SerialPort(laserPortName, LASER_BAUD_RATE);
                laserPort.DataBits = 8;
                laserPort.StopBits = StopBits.One;
                laserPort.Parity = Parity.None;
                laserPort.DtrEnable = false; //Data terminal ready not in use (pin 4)

                // Opening the serial port
                laserPort.Open();

                // Setting laser serial port name to corresponding label
                laserPortLabel.Text = laserPortName;

                // Giving messagehandler when data is received to serial port
                laserPort.DataReceived += new SerialDataReceivedEventHandler(LaserCommunicationHandler);
            }
            catch (Exception ex)
            {
                // oh no, again catching general exception
                writeLogFile(ex.Message);
                System.Console.WriteLine(ex.Message);
                MessageBox.Show("Communication error with laser", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Initialize webcam
        /// </summary>
        private void webcam()
        { 
            try
            {
                // enumerate video devices
                videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                // create video source
                videoSource = new VideoCaptureDevice(videoDevices[0].MonikerString);
                // set NewFrame event handler
                videoSource.NewFrame += new NewFrameEventHandler(video_NewFrame);
                // start the video source
                videoSource.Start();
            }
            catch(Exception ex)
            {
                // This try-catch horror continues
                System.Console.WriteLine(ex.Message);
                MessageBox.Show("Webcam error", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Process new image from videosource
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="eventArgs"></param>
        private void video_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            Image webcamImage = (Bitmap)eventArgs.Frame.Clone();
            chamberImage = (Bitmap)webcamImage.Clone();
            updateImage();

        }

        /// <summary>
        /// Setting image to picturebox2 with thread-safe manner
        /// </summary>
        /// <param name="bitmap"></param>
        private void updateImage()
        {
            if (pictureBox2.InvokeRequired)
            { pictureBox2.Invoke(new Action(() => pictureBox2.BackgroundImage = chamberImage)); }
            else
            {
                pictureBox2.BackgroundImage = Image.FromFile("chamberImage.jpeg"); ;
            }
        }


        /// <summary>
        /// Handles incoming messages from the device
        /// </summary>
        private void ArduinoCommunicationHandler(object sender, SerialDataReceivedEventArgs e)
        {
            if (port.IsOpen)
            {
                try
                {
                    SerialPort sp = (SerialPort)sender;
                    string device_msg = sp.ReadLine();
                    System.Console.WriteLine(device_msg);
                    if (device_msg.StartsWith("DATA"))
                    {
                        string data=device_msg.Split('[', ']')[1];
                        data.Replace(',', '.');
                        string[] data_splitted = data.Split(' ');
                        // Reading data values to variables
                        try
                        {
                            ArFlow = Double.Parse(data_splitted[1], CultureInfo.InvariantCulture);
                            N2Flow = Double.Parse(data_splitted[0], CultureInfo.InvariantCulture);
                            chamberTemperature = Double.Parse(data_splitted[2], CultureInfo.InvariantCulture);
                        }
                        catch(Exception ex){ System.Console.WriteLine(ex.Message); }
                        Int32.TryParse(data_splitted[3], out motorPosition);
                        // updating data labels to show data values
                        updateDataLabels();
                    }
                    else
                    {
                        writeLogFile("Device: " + device_msg);
                    }
                }
                catch (IOException ex)
                {
                    System.Console.WriteLine(ex.Message);
                }
            }

        }

        /// <summary>
        /// Handles communication to laser
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LaserCommunicationHandler(object sender2, SerialDataReceivedEventArgs e)
        {
            try
            {
                SerialPort sp2 = (SerialPort)sender2;
                buffer += sp2.ReadExisting();

                //test for termination character in buffer
                if (buffer.Contains("]"))
                {
                    var laser_messages = buffer.Split('[',']');
                    writeLogFile("NL->PC: " + buffer);
                    if (buffer.Contains("ERROR"))
                    {
                        MessageBox.Show("Error encoutered", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    //Process each message separately
                    foreach ( string msg in laser_messages)
                    {
                        try
                        {
                            // Extract actual message body from incoming 
                            // message of the form [ReceiverName:MessageBody\SenderName],
                            // so in this case [PC:message\NL]
                            var laser_msg = msg.Split(':', '\\')[1];
                            //writeLogFile("NL->PC: " + laser_msg);

                            // Decide what to do depending on received message

                            // Selected mode information message received
                            if (laser_msg.StartsWith("E0"))
                            {
                                // Message of the form E0/S? where ? is integer indicating current output mode
                                Int32.TryParse(laser_msg.Split('S')[1],out int mode);
                                if (mode == 0) { laserMode = "OFF"; }
                                if (mode == 1) { laserMode = "ADJUST"; }
                                if (mode == 2) { laserMode = "MAX"; }
                            }

                            //Status message received handler
                            if(laser_msg.StartsWith("READY")|| laser_msg.StartsWith("START"))
                            {
                                // check if message contains problem code
                                if (laser_msg.ToLower().Contains('='))
                                {
                                    Int32.TryParse(laser_msg.Split('=')[1], out int problemNumber);
                                    // Find out what problem there is
                                    switch (problemNumber)
                                    {
                                        case 1:
                                            MessageBox.Show("Laser not ready", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                            laserStatus = "Not ready";
                                            break;
                                        case 2:
                                            MessageBox.Show("Laser overheated", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                            laserStatus = "Overheated";
                                            break;
                                        case 4:
                                            MessageBox.Show("Flash lamp error", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                            laserStatus = "Flash lamp error";
                                            break;
                                        case 8:
                                            MessageBox.Show("Interlock error", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                            laserStatus = "Interlock error";
                                            break;
                                        case 16:
                                            MessageBox.Show("Cover error", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                            laserStatus = "Cover error";
                                            break;


                                    }
                                }
                                else
                                {
                                    // No problems detected
                                    if (laser_msg.StartsWith("READY")) {laserStatus = "Ready"; }
                                    else
                                    {
                                        MessageBox.Show("Laser started");
                                        laserStatus = "Busy";
                                    }

                                }
                            }
                            // Laser temperature message received
                            // Message of the form U2/S? where ? is double corresponding to measured cooling water temperature
                            if (laser_msg.StartsWith("U2")) { laserTemp=Double.Parse(laser_msg.Split('S')[1], CultureInfo.InvariantCulture); }
                            // Max output delay message received
                            // Message of the form D0/S? where ? is integer corresponding to measured cooling water temperature
                            if (laser_msg.StartsWith("D0")) { Int32.TryParse(laser_msg.Split('S')[1], out eoDelayMa); }

                            if (laser_msg.StartsWith("P0")) { Int32.TryParse(laser_msg.Split('S')[1], out packPulses); }
                            // Max output delay message received
                            // Message of the form D1/S? where ? is integer corresponding to measured cooling water temperature
                            // Last message from status query, so now messagebox with laser status values is displayed
                            if (laser_msg.StartsWith("D1"))
                            {
                                Int32.TryParse(laser_msg.Split('S')[1], out eoDelayAd);
                                MessageBox.Show("Laser status: " + laserStatus + Environment.NewLine +
                                    "Laser mode: " + laserMode + Environment.NewLine+
                                    "Cooling water temperature: " + string.Format("{0}°C", laserTemp.ToString())+Environment.NewLine+
                                    "Electro-optics delay in MAX: " + eoDelayMa + Environment.NewLine+
                                    "Electro-optics delay in ADJ: " + eoDelayAd + Environment.NewLine+
                                    "Number of PACK pulses: " + packPulses + Environment.NewLine);
                            }
                        }
                        catch (System.IndexOutOfRangeException ex) { /*Nothing to see here*/ }
                    }
                    buffer = "";
                }
            }
            catch (IOException ex)
            {
                System.Console.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Updates data labels more thread-safe
        /// </summary>
        private void updateDataLabels()
        {
            // invoke every label with lambda expressions if invoke is required
            if (ArFlow_label.InvokeRequired)
            { ArFlow_label.Invoke(new Action(() => ArFlow_label.Text = String.Format("{0:0.00}", ArFlow))); }
            if (N2FLow_label.InvokeRequired)
            { N2FLow_label.Invoke(new Action(() => N2FLow_label.Text = String.Format("{0:0.00}", N2Flow))); }
            if (Temperature_label.InvokeRequired)
            {
                Temperature_label.Invoke(new Action(() => Temperature_label.Text = String.Format("{0:0.0}°C", chamberTemperature)));
                if (chamberTemperature < 30) { Temperature_label.ForeColor = Color.Lime; }
                if (chamberTemperature >= 30 && chamberTemperature<50 ) { Temperature_label.ForeColor = Color.Orange; }
                if (chamberTemperature >= 50) { Temperature_label.ForeColor = Color.Red; }

            }
            if (motorPosition_label.InvokeRequired)
            { motorPosition_label.Invoke(new Action(() => motorPosition_label.Text = motorPosition.ToString())); }
            else
            {
                // Set label texts normally
                ArFlow_label.Text = String.Format("{0:0.00}", ArFlow);
                N2FLow_label.Text = String.Format("{0:0.00}", ArFlow);
                Temperature_label.Text = String.Format("{0:0.0}°C", chamberTemperature);
                motorPosition_label.Text = motorPosition.ToString();
            }
        }

        /// <summary>
        /// Sends message to device via open serial port 
        /// </summary>
        /// <param name="message"></param>
        private void WriteToDevice(string message)
        {
                // Check if there is device in the port
                if (port == null)
                {
                    MessageBox.Show("No USB-devices detected", "FastSerial 2.0", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                try
                {
                    if (port.IsOpen)
                    {
                        port.WriteLine(message+"\n");
                    }
                    else
                    {
                        port.Open();
                        port.WriteLine(message + "\n");
                    }
                writeLogFile("PLDcontrol: " + message); // Writing sended message to logfile
            }
                catch (Exception ex)
                {
                    System.Console.WriteLine(ex);
                    writeLogFile("Message sending error:" + ex);
                    MessageBox.Show("Error", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
        }

        /// <summary>
        /// Sends message to device via open serial port 
        /// </summary>
        /// <param name="message"></param>
        private void SendMsgToLaser(string message)
        {
            try
            {
                if (laserPort.IsOpen)
                {
                    laserPort.WriteLine(message + "\n");
                }
                else
                {
                    laserPort.Open();
                    laserPort.WriteLine(message + "\n");
                }
                writeLogFile("PC->NL: " + message); // Writing sended message to logfile
            }
            catch (Exception ex)
            {
                writeLogFile("Message sending error to laser:" + ex.Message);
                MessageBox.Show("Error", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Writes logfile with thread-safe manner
        /// </summary>
        /// <param name="message">message to write to logfile</param>
        private async void writeLogFile(string message)
        {
            // wait until file is unlocked by filelock
            while (fileLock.locking) { await Task.Delay(ASYNC_DELAY); }
            if(!fileLock.locking){
                //Set filelock true while writing to it
                fileLock.locking = true;
                String timeStamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"); // Timestamp to logfile
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(filename, true))
                {
                    file.WriteLine(timeStamp + " > " + message + "\n");
                }
                // Unlock file
                fileLock.locking = false;
            }

        }


        /// <summary>
        /// Opening the console viewer
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void consoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Make new consolewindow item
            ConsoleWindow consoleWindow = new ConsoleWindow(fileLock);
            consoleWindow.Text = "PLD Control";
            consoleWindow.Show();
            writeLogFile("Logfile viewing started");
        }


        /// <summary>
        /// Sending message to device when gas flow rate needs to be adjusted.
        /// Value is set by trackbar and value copied to numeric control
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void sendGasFlow(object sender, EventArgs e)
        {
            if (!hold)
            {
                double sccm = 10.0 * (trackBar1.Value * 1.0 / trackBar1.Maximum); // Scaling trackbar value to 10 sccm
                double sccm2 = 10.0 * (trackBar2.Value * 1.0 / trackBar2.Maximum); // Scaling trackbar value to 10 sccm
                string msg = "GAS[" + String.Format("{0:0.000}%", sccm) + " " + String.Format("{0:0.000}%", sccm2) + "]";
                WriteToDevice(msg);
                hold = true;
                N2FlowNumericControl.Value = (decimal)sccm;
                ArFlowNumericControl.Value = (decimal)sccm2;
                hold = false;
            }
        }

        /// <summary>
        /// Sends gas flow setpoint to device
        /// Value is set by numeric control and copied to trackbar
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FlowNumericControl_ValueChanged(object sender, EventArgs e)
        {
            if (!hold)
            {
                double sccm = (double)N2FlowNumericControl.Value;
                double sccm2 = (double)ArFlowNumericControl.Value;
                string msg = "GAS[" + String.Format("{0:0.000}%", sccm) + " " + String.Format("{0:0.000}%", sccm2) + "]";
                WriteToDevice(msg);
                hold = true;
                trackBar1.Value = (int)(sccm * trackBar1.Maximum / 10);
                trackBar2.Value = (int)(sccm2 * trackBar2.Maximum / 10);
                hold = false;
            }
        }

        /// <summary>
        /// Defines arduino serial again when selected port is changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void toolStripComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            DefineSerial();
        }

        private void menuStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            //
        }

        private void settingsToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            // SETTINGS
        }

        private void loadControlFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Control file
        }

        
        /// <summary>
        /// Style-none ikkuna siirreltäväksi
        /// </summary>
        /// <param name="m"></param>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST)
                m.Result = (IntPtr)(HT_CAPTION);
        }

        private const int WM_NCHITTEST = 0x84;
        private const int HT_CLIENT = 0x1;
        private const int HT_CAPTION = 0x2;
        
        /// <summary>
        /// Closes window, custom button
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void closeWindowButton_Click(object sender, EventArgs e)
        {
            //Close();
            Application.ExitThread();
            Environment.Exit(Environment.ExitCode);
        }

        /// <summary>
        /// Sends stop message to step motor
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void motorStopButton_Click(object sender, EventArgs e)
        {
            WriteToDevice("STOP");
        }

        /// <summary>
        /// Starts to move stepmotor to left
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void motorLeftButton_Click(object sender, EventArgs e)
        {
            WriteToDevice("LEFT");
        }


        /// <summary>
        /// Starts to move stepmotor to right
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void motorRightButton_Click(object sender, EventArgs e)
        {
            WriteToDevice("RIGHT");
        }

        /// <summary>
        /// Moves stepmotor to wanted position
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void goToButton_Click(object sender, EventArgs e)
        {
            int val = (int)goToValueNumeric.Value; // Value where motor should move
            WriteToDevice("GOTO[" + val + "]");
        }

        /// <summary>
        /// Handling event when sweep enable checkbox checked changed
        /// and enbales other controls
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void sweepCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (sweepCheckBox.Checked)
            {
                sweepButton.ForeColor = Color.Black;
                rangeNumeric.Enabled = true;
                midPointNumeric.Enabled = true;
            }
            else
            {
                sweepButton.ForeColor = Color.Gray;
                rangeNumeric.Enabled = false;
                midPointNumeric.Enabled = false;
            }
        }

        /// <summary>
        /// Starts sweep if sweep is enabled
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void sweepButton_Click(object sender, EventArgs e)
        {
            if (sweepCheckBox.Checked)
            {
                int range = (int) rangeNumeric.Value;
                int midpoint = (int)midPointNumeric.Value;
                WriteToDevice("SWEEP[" + range + " "+ midpoint+"]");
            }

        }

        /// <summary>
        /// Minimizes window, custom button
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void minimizeWindowButton_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }
        
        /// <summary>
        /// Switches laser on or off
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void laserOnOffButton_Click(object sender, EventArgs e)
        {
            // Laser is switched OFF
            if (LaserOn)
            {
                laserSTOP(sender,e);
            }
            // Laser is switched ON
            else
            {
                if (!offradioButton.Checked) { SendMsgToLaser("[NL:START\\PC]"); } // Sending start message to laser
                stopwatch.Start();
                // Starting timer if that is enabled
                if (timerCheckBox.Checked)
                {
                    // Calculating timeinterval in seconds given by numeric controls
                    int timerSecs = (int)secNumericControl.Value;
                    int timerMin = (int)minNumericControl.Value;
                    int timerValue = timerMin * 60 + timerSecs;
                    laserTimer.Interval = timerValue*1000; // Interval in milliseconds
                    laserTimer.Tick += new EventHandler(laserSTOP);
                    laserTimer.Start();
                }
                // Changing button appearance
                laserOnOffButton.Text = "STOP";
                laserOnOffButton.BackColor = Color.Red;
                // Changin images to corresponding laser off situaton
                lasersignPictureBox.Image = Properties.Resources.laser_warning;
                lasersignPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                pictureBox1.BackgroundImage = Properties.Resources.schem10_on;
                // Changing laserOn state
                LaserOn = true;
            }
        }

        /// <summary>
        /// Handler for laser max radiobutton click.
        /// Sends message to laser that output mode should be changed to MAX
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void maxButton_CheckedChanged(object sender, EventArgs e)
        {
            if (maxButton.Checked)
            {
                SendMsgToLaser("[NL:E0/S2\\PC]");// Set output to max
                MessageBox.Show("Laser output mode set to max", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
                offradioButton.Checked = false;
                adjRadioButton.Checked = false;
            }
        }

        /// <summary>
        /// Handler for laser off radiobutton click
        /// Sends message to laser that it should put electro-optics off
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void offradioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (offradioButton.Checked&&!startup)
            {
                SendMsgToLaser("[NL:E0/S0\\PC]");// Set electrooptics off
                MessageBox.Show("Laser electro-optics switched off", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
                maxButton.Checked = false;
                adjRadioButton.Checked = false;
            }
        }

        /// <summary>
        /// Handler for laser off radiobutton click
        /// Sends message to laser that output mode is adjust
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void adjRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (adjRadioButton.Checked)
            {
                SendMsgToLaser("[NL:E0/S1\\PC]");// Set output to adjust mode
                MessageBox.Show("Laser output mode set to adjust", "PLDControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
                offradioButton.Checked = false;
                maxButton.Checked = false;
            }
        }

        /// <summary>
        /// Stopping the laser
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void laserSTOP(object sender, EventArgs e)
        {
            stopwatch.Stop();
            SendMsgToLaser("[NL:STOP\\PC]"); // Send stop command to laser
            laserTimer.Stop();
            //Resetting timer, kind of a hack but don't know better way
            laserTimer.Start();
            laserTimer.Stop();
            // Changing button appearance
            laserOnOffButton.Text = "START";
            laserOnOffButton.BackColor = Color.Lime;
            // Changing images to corresponding laser off situaton
            lasersignPictureBox.Image = null;
            pictureBox1.BackgroundImage = Properties.Resources.schem10;
            // Changing laserOn state
            LaserOn = false;
        }

        /// <summary>
        /// Sends status query to laser
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void laserStatusButton_Click(object sender, EventArgs e)
        {
            //Send status messages to laser with different thread than UI thread
            Thread t = new Thread(new ThreadStart(laserSendThread));
            t.Start();
            this.UseWaitCursor = true;
            t.Join();
            this.UseWaitCursor = false;
        }

        /// <summary>
        /// Asking status information from laser.
        /// Thread.sleep is to wait the answer before asking new question, 
        /// this makes communication to be more reliable.
        /// </summary>
        private void laserSendThread(){

            // Ask status from laser
            SendMsgToLaser("[NL:SAY\\PC]"); // Message is [NL:SAY\PC], but backslash is special character and hence needs to be doubled
            // Ask laser mode 
            Thread.Sleep(200);
            SendMsgToLaser("[NL:E0/?\\PC]");
            Thread.Sleep(200);
            // Ask cooling water temperature from laser
            SendMsgToLaser("[NL:U2/?\\PC]");
            Thread.Sleep(200);
            SendMsgToLaser("[NL:P0/?\\PC]");
            Thread.Sleep(200);
            // Ask electro-optics delay in max mode
            SendMsgToLaser("[NL:D0/?\\PC]");
            Thread.Sleep(200);
            // Ask electro-optics delay in adjust mode
            SendMsgToLaser("[NL:D1/?\\PC]");
            Thread.Sleep(200);
            SendMsgToLaser("[NL:P0/?\\PC]");
            Thread.Sleep(200);
        }
        /// <summary>
        /// Handles event when timer for laser enabled or disabled.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void timerCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            // Disable or enable time controls depending timer bool value
            minNumericControl.Enabled = timerCheckBox.Checked;
            secNumericControl.Enabled = timerCheckBox.Checked;
        }


        /// <summary>
        /// Resets the stopwatch
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void timeClearButton_Click(object sender, EventArgs e)
        {
            stopwatch.Reset();
        }

        /// <summary>
        /// Sets time to timer label
        /// </summary>
        /// <param name="text"></param>
        private void SetTimeText(string text)
        {
            if (this.timerLabel.InvokeRequired)
            {
                SetTextCallback d = new SetTextCallback(SetTimeText);
                try { this.Invoke(d, new object[] { text }); }
                catch (ObjectDisposedException){/* Dont bother to do anything*/}
            }
            else
            {
                this.timerLabel.Text = text;
            }
        }

        /*
         * Useless event handlers
         * waiting for better times when hey are needed
         */

        private void goToValueNumeric_ValueChanged(object sender, EventArgs e)
        {
            //
        }

        private void secNumericControl_ValueChanged(object sender, EventArgs e)
        {
            // Nothing to do here
        }

        private void minNumericControl_ValueChanged(object sender, EventArgs e)
        {
            // Nothing to do here
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            //
        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {
            //
        }

        private void splitContainer1_Panel2_Paint(object sender, PaintEventArgs e)
        {
            //
        }

        private void communicationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //
        }

        private void currentPortLabel_Click(object sender, EventArgs e)
        {
            //
        }

        private void currentPortLabel_Click_1(object sender, EventArgs e)
        {
            //
        }

        private void label1_Click(object sender, EventArgs e)
        {
            //
        }

        private void N2FLow_label_Click(object sender, EventArgs e)
        {
            //
        }

        private void ArFlow_label_Click(object sender, EventArgs e)
        {
            //
        }

        private void Temperature_label_Click(object sender, EventArgs e)
        {
            //
        }

        private void motorPosition_label_Click(object sender, EventArgs e)
        {
            //
        }

        private void label3_Click(object sender, EventArgs e)
        {
            //
        }

        private void label4_Click(object sender, EventArgs e)
        {
            //
        }

        private void label7_Click(object sender, EventArgs e)
        {
            //
        }

        private void serialPortsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //
        }

        private void pictureBox2_Click(object sender, EventArgs e)
        {
            //
        }

        private void timerLabel_Click(object sender, EventArgs e)
        {
            //
        }

        private void panel6_Paint(object sender, PaintEventArgs e)
        {
            //
        }

        private void Label6_Click(object sender, EventArgs e)
        {
            //
        }
    }
}
