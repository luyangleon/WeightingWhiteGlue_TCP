using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using WeightingWhiteGlue.model;

namespace WeightingWhiteGlue
{
    public partial class MainForm : Form
    {
        //ww:Gross:毛重,wn:Net:净重,wt:Tare:皮重
        // ====== TCP 相关字段 ======
        private TcpClient _tcpClient;
        private NetworkStream _networkStream;
        private Thread _receiveThread;
        private bool _isConnected = false;
        private readonly object _lockObject = new object(); // 线程安全锁

        private string _currentWeight = "0.000";
        private string _currentUnit = "kg";
        private string _currentWeightType= "wn";
        private string _currentWeightTypeName= "净重";
        private bool _isStable = false;

        private bool _isReadingData = false;
        private DateTime _lastReadTime = DateTime.MinValue;
        private bool _isBeginWeighing = false;

        private readonly static string _lastAutoWeightFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LastAutoWeight.txt");
        private readonly static string _lastIdFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LastId.txt");
        private static string _lastId = ReadLastId();

        private SQLDBHelper SA = new SQLDBHelper();
        private OdbcHelper OA = new OdbcHelper();

        // 防抖相关字段
        private System.Windows.Forms.Timer _debounceTimer = new System.Windows.Forms.Timer();
        private DateTime _lastCommandTime = DateTime.MinValue;
        private const int _DebounceInterval = 1000;
        
        private static readonly Regex _WeightPattern = new Regex(@"(ww|wn|wt)\s*(-?\d*\.?\d+)(kg|g|lb)?", 
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        
        public MainForm()
        {
            InitializeComponent();
            InitCMB();
            lblPort.Text = "串口:" + Utils.GetParameterValue("Port");
            lblBaud.Text = "波特率:" + Utils.GetParameterValue("BaudRate");

            _debounceTimer.Interval = _DebounceInterval;
            _debounceTimer.Tick += DebounceTimer_Tick;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            //UpdateUI(() => UpdateDGV());
        }

        private void UpdateDGV()
        {
            if (string.IsNullOrEmpty(this.cmbConvertMachine.Text)) return;
            // 绑定DataGridView
            DataTable ds = SA.GetDataTable($@"
SELECT TOP 1000 
[Id],[Plant],[MachineId],[Shift],[WeighingType],[WaterRate],[WeighingWeightBegin],[WeighingWeightEnd],[WeighingTimeBegin],[WeighingTimeEnd],[Site]
FROM WeighingRecord 
Order By WeighingTimeBegin DESC"
, Utils.GetParameterValue("DBConnStr"));
            dgvRecords.DataSource = ds;
            // 设置第一行高亮
            HighlightFirstRow();
        }

        private void dgvRecords_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (this.dgvRecords.Columns[e.ColumnIndex].Name == "Shift")
            {
                if (e.Value != null)
                {
                    if (e.Value.ToString() == "1")
                    {
                        e.Value = "忠班";
                    }
                    else if (e.Value.ToString() == "2")
                    {
                        e.Value = "义班";
                    }
                }
            }
            if (this.dgvRecords.Columns[e.ColumnIndex].Name == "WeighingType")
            {
                if (e.Value != null)
                {
                    switch (e.Value)
                    {
                        case "wn":
                            e.Value = "净重";
                            break;
                        case "ww":
                            e.Value = "毛重";
                            break;
                        case "wt":
                            e.Value = "皮重";
                            break;
                    }
                }
            }
        }

        private void InitCMB()
        {
            // 厂区初始化
            string plant = Utils.GetParameterValue("Plant") ?? "W6";
            List<string> plantList = Utils.GetParameterValue("Plants")?.Split('|').ToList();
            cmbPlant.Items.Clear();
            cmbPlant.Items.AddRange(plantList?.ToArray());
            cmbPlant.SelectedItem = plant;
            // 机台初始化
            List<string> machineList = Utils.GetParameterValue($"{plant}ConvertMachine")?.Split('|').ToList();
            cmbConvertMachine.Items.Clear();
            cmbConvertMachine.Items.AddRange(machineList?.ToArray());
            // 班次初始化
            cmbShift.Items.Clear();
            List<ComboBoxItem> shiftList = new List<ComboBoxItem>
            {
                new ComboBoxItem("忠班", "1"),
                new ComboBoxItem("义班", "2")
            };
            cmbShift.DataSource = shiftList;
            cmbShift.SelectedIndex = 0;
            // 站点初始化
            cmbSite.Items.Clear();
            List<string> siteList = Utils.GetParameterValue("Sites")?.Split('|').ToList();
            cmbSite.Items.AddRange(siteList?.ToArray());
            cmbSite.SelectedIndex = 0;
        }

        // ====== 连接电子秤（TCP Client）======
        private void ConnectToScale()
        {
            try
            {
                string ip = Utils.GetParameterValue("ScaleIP") ?? "192.168.0.7";
                int port = int.Parse(Utils.GetParameterValue("ScalePort") ?? "23");

                _tcpClient = new TcpClient();
                _tcpClient.Connect(ip, port);
                _networkStream = _tcpClient.GetStream();
                _isConnected = true;

                // 启动接收线程
                _receiveThread = new Thread(ReceiveDataLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Start();

                UpdateUI(() => lblStatus.Text = $"已连接 {ip}:{port}");
                Log($"[INFO] TCP 连接成功: {ip}:{port}");
            }
            catch (Exception ex)
            {
                _isConnected = false;
                UpdateUI(() => lblStatus.Text = "连接失败");
                Log($"[ERROR] TCP 连接失败: {ex.Message}");
                MessageBox.Show($"无法连接电子秤：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        // ====== 断开连接 ======
        private void DisconnectFromScale()
        {
            lock (_lockObject)
            {
                _isConnected = false;
                _networkStream?.Close();
                _tcpClient?.Close();
                _networkStream = null;
                _tcpClient = null;
            }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(1000);
            }
        }
        // ====== 接收数据循环（后台线程）======
        private void ReceiveDataLoop()
        {
            byte[] buffer = new byte[1024];
            StringBuilder sb = new StringBuilder();

            while (_isConnected)
            {
                try
                {
                    if (_networkStream?.DataAvailable == true)
                    {
                        int bytesRead = _networkStream.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            string data = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                            sb.Append(data);

                            // 假设电子秤以 \r\n 或 \n 结尾（根据实际调整）
                            if (data.IndexOf('\n') >= 0)
                            {
                                string fullLine = sb.ToString().Trim();
                                sb.Clear();

                                // 转发到 UI 线程处理
                                UpdateUI(() => ProcessReceivedData(fullLine));
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(50);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] 接收异常: {ex.Message}");
                    break;
                }
            }

            // 连接断开后更新 UI
            UpdateUI(() =>
            {
                lblStatus.Text = "连接已断开";
                btnConnect.Text = "连接电子秤";
            });
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(cmbPlant.Text) || string.IsNullOrEmpty(cmbConvertMachine.Text)) 
                { 
                    MessageBox.Show("请先选择厂区和机台！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                cmbPlant.Enabled = false;
                cmbConvertMachine.Enabled = false;
                cmbShift.Enabled = false;
                cmbSite.Enabled = false;
                btnConnect.Enabled = false;
                btnDisconnect.Enabled = true;
                btnZero.Enabled = true;
                btnTare.Enabled = true;
                btnRead.Enabled = true;
                btnReadEnd.Enabled = true;
                chkAutoRead.Enabled = true;
                numWaterRate.Enabled = true;

                ConnectToScale();
                // 连接成功后，初始化DataGridView
                UpdateUI(() => UpdateDGV());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"串口连接失败: {ex.Message}");
            }
        }

        private void BtnDisconnect_Click(object sender, EventArgs e)
        {
            try
            {
                DisconnectFromScale();

                cmbPlant.Enabled = true;
                cmbConvertMachine.Enabled = true;
                cmbShift.Enabled = true;
                cmbSite.Enabled = true;
                btnConnect.Enabled = true;
                btnDisconnect.Enabled = false;
                btnZero.Enabled = false;
                btnTare.Enabled = false;
                btnRead.Enabled = false;
                btnReadEnd.Enabled = false;
                chkAutoRead.Enabled = false;
                numWaterRate.Enabled = false;

                UpdateLblStatus("状态: 未连接", Color.Black);

                Log("串口已断开");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"断开失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"串口断开失败: {ex.Message}");
            }
        }

        private void BtnZero_Click(object sender, EventArgs e)
        {
            SendCommand("Z");
            UpdateLblStatus("状态: 已发送Z置零命令", Color.Green);
        }

        private void BtnTare_Click(object sender, EventArgs e)
        {
            SendCommand("T");
            UpdateLblStatus("状态: 已发送T去皮命令", Color.Green);
        }

        /// <summary>
        /// 开始称重
        /// </summary>
        private void BtnRead_Click(object sender, EventArgs e)
        {
            // 计算与上次发送命令的时间间隔
            TimeSpan timeSinceLastCommand = DateTime.Now - _lastCommandTime;
            // 如果间隔大于等于防抖间隔，立即发送命令
            if (timeSinceLastCommand >= TimeSpan.FromMilliseconds(_DebounceInterval))
            {
                if (!_isReadingData)
                {
                    // 开始称重的标记
                    _isBeginWeighing = true;
                    // 开始读取：设置读取状态，准备接收数据
                    _isReadingData = true;
                    _lastReadTime = DateTime.MinValue;
                    SendCommand("R");
                    UpdateLblStatus("状态: 已发送R读取命令，正在接收数据...", Color.Orange);
                }
                else
                {
                    // 停止读取：重置读取状态
                    _isReadingData = false;
                }
            }            
        }
        /// <summary>
        /// 结束称重
        /// </summary>
        private void btnReadEnd_Click(object sender, EventArgs e)
        {
            // 计算与上次发送命令的时间间隔
            TimeSpan timeSinceLastCommand = DateTime.Now - _lastCommandTime;
            // 如果间隔大于等于防抖间隔，立即发送命令
            if (timeSinceLastCommand >= TimeSpan.FromMilliseconds(_DebounceInterval))
            {
                if (!_isReadingData)
                {
                    _isBeginWeighing = false;
                    // 开始读取：设置读取状态，准备接收数据
                    _isReadingData = true;
                    _lastReadTime = DateTime.MinValue;
                    SendCommand("R");
                    UpdateLblStatus("状态: 已发送R读取命令，正在接收数据...", Color.Orange);
                }
                else
                {
                    // 停止读取：重置读取状态
                    _isReadingData = false;
                }
            }
        }

        // ====== 发送命令（如 R / T / Z）======
        private void SendCommand(string command)
        {
            if (!_isConnected || _networkStream == null || !_networkStream.CanWrite)
            {
                Log("[WARN] 无法发送：未连接");
                return;
            }

            if ((DateTime.Now - _lastCommandTime).TotalMilliseconds < _DebounceInterval)
            {
                return; // 防抖
            }

            try
            {
                string msg = command + "\r\n"; // 多数设备需要回车换行
                byte[] data = Encoding.ASCII.GetBytes(msg);
                _networkStream.Write(data, 0, data.Length);
                _networkStream.Flush();
                _lastCommandTime = DateTime.Now;

                Log($"[SEND] {msg.Trim()}");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] 发送失败: {ex.Message}");
                DisconnectFromScale();
            }
        }

        /// <summary>
        /// 防抖计时器触发事件
        /// </summary>
        private void DebounceTimer_Tick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
        }

        private void ProcessSingleLineData(string lineData)
        {
            try
            {
                // 使用静态正则表达式提取有效重量数据，查找字符串中任意位置的匹配
                Match match = _WeightPattern.Match(lineData);
                
                if (match.Success)
                {
                    ProcessMatchResult(match, "[ReadLine读取完成]");
                }
                else
                {
                    // 尝试查找所有匹配，处理包含噪声的数据
                    MatchCollection matches = _WeightPattern.Matches(lineData);
                    if (matches.Count > 0)
                    {
                        ProcessMatchResult(matches[0], "[ReadLine读取完成]");
                    }
                    else
                    {
                        Log($"[ReadLine警告]: 未找到有效重量数据: {lineData}");
                        _isReadingData = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"单行数据处理异常: {ex.Message}");
            }
        }

        private void ProcessReceivedData(string rawData)
        {
            Log($"[RECV] {rawData}");

            // 使用你原有的正则表达式解析
            var match = Regex.Match(rawData, @"(ww|wn|wt)\s*(-?\d*\.?\d+)(kg|g|lb)?", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                ProcessMatchResult(match, "[单次读取完成]"); // 你原有的方法，无需修改！
            }
            else
            {
                // 可选：显示未识别数据
                // lblWeight.Text = "格式错误";
            }
        }

        /// <summary>
        /// 处理匹配到的重量数据
        /// </summary>
        /// <param name="match">正则表达式匹配结果</param>
        /// <param name="logPrefix">日志前缀</param>
        private void ProcessMatchResult(Match match, string logPrefix)
        {
            if (match.Success)
            {
                string typeCode = match.Groups[1].Value.ToLower();
                string weightStr = match.Groups[2].Value;
                string unit = match.Groups[3].Success ? match.Groups[3].Value : "kg";
                
                ProcessWeightData(typeCode, weightStr, unit);

                // 读取到一条有效数据后，自动停止读取
                _isReadingData = false;
                
                // 更新UI状态
                UpdateUI(() =>
                {
                    UpdateLblStatus("状态: 已读取一条数据，自动停止", Color.Black);
                });

                Log($"{logPrefix}: 已读取一条数据并自动停止");
            }
        }

        private void ProcessWeightData(string typeCode, string weightStr, string unit)
        {
            try
            {
                // 验证重量是否为有效数字
                if (!decimal.TryParse(weightStr, out decimal weight))
                {
                    Log($"[警告]: 无效的重量值: {weightStr}");
                    return;
                }

                // 验证类型代码
                if (typeCode != "ww" && typeCode != "wn" && typeCode != "wt")
                {
                    Log($"[警告]: 无效的类型代码: {typeCode}");
                    return;
                }

                _currentWeightType = typeCode;
                _currentWeight = weight.ToString();
                _currentUnit = unit;

                switch (typeCode)
                {
                    case "ww":
                        _currentWeightTypeName = "毛重";
                        break;
                    case "wn":
                        _currentWeightTypeName = "净重";
                        break;
                    case "wt":
                        _currentWeightTypeName = "皮重";
                        break;
                }

                // 检查是否是新数据（避免短时间内重复处理）
                if (DateTime.Now - _lastReadTime > TimeSpan.FromSeconds(1))
                {
                    // 线程安全的UI更新
                    UpdateWeightDisplaySafe(_currentWeightTypeName);
                    _lastReadTime = DateTime.Now;

                    // 保存到DataGridView
                    //UpdateUI(() => SaveToDGV());
                    UpdateUI(() =>
                    {
                        if (chkAutoRead.Checked)
                            AutoSave(); // 自动称重
                        else
                            SaveToDGV(); // 手动称重
                    });
                    UpdateUI(() => UpdateDGV());
                    Log($"[解析成功]: {_currentWeightTypeName} = {weightStr}{unit}");
                }
            }
            catch (Exception ex)
            {
                Log($"[解析错误]: {ex.Message} - 类型: {typeCode}, 重量: {weightStr}, 单位: {unit}");
                UpdateUI(() =>
                {
                    UpdateLblStatus($"解析数据错误: {ex.Message}", Color.Red);
                });
            }
        }

        /// <summary>
        /// 通用的UI更新方法，线程安全
        /// </summary>
        /// <param name="action">要执行的UI更新操作</param>
        private void UpdateUI(Action action)
        {
            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        
        /// <summary>
        /// 线程安全的UI更新方法
        /// </summary>
        private void UpdateWeightDisplaySafe(string typeName)
        {
            UpdateUI(() => UpdateWeightDisplay(typeName));
        }

        private void UpdateWeightDisplay(string typeName)
        {
            lblWeight.Text = _currentWeight;
            lblUnit.Text = _currentUnit;
            lblWeightType.Text = typeName;

            // 判断是否稳定（简单判断：非零且不变化）
            _isStable = !_currentWeight.Trim().StartsWith("0.000");
            pnlIndicator.BackColor = _isStable ? Color.Lime : Color.Gray;

            UpdateLblStatus($"状态: 接收成功 - {typeName}: {_currentWeight}{_currentUnit}", Color.Green);
        }

        private void ChkAutoRead_CheckedChanged(object sender, EventArgs e)
        {
            if (chkAutoRead.Checked)
            {
                autoReadTimer.Start();
                UpdateLblStatus("状态: 自动读取已启动", Color.Green);
                Log("自动读取已启动");
            }
            else
            {
                autoReadTimer.Stop();
                UpdateLblStatus("状态: 自动读取已停止", Color.Black);
                Log("自动读取已停止");
            }
        }

        /// <summary>
        /// 每隔10分钟自动称重一次
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void AutoReadTimer_Tick(object sender, EventArgs e)
        {
            if (!_isReadingData)
            {
                // 设置读取状态，准备接收数据
                _isReadingData = true;
                SendCommand("R");
            }
        }

        #region 自动称重模块

        /// <summary>
        /// 记录上次称重的重量到本地文件
        /// </summary>
        /// <param name="weight"></param>
        private static void WriteLastAutoWeight(double weight)
        {
            File.WriteAllText(_lastAutoWeightFile, weight.ToString());
        }
        /// <summary>
        /// 从本地文件读取上次称重的重量
        /// </summary>
        /// <returns></returns>
        private static double ReadLastAutoWeight()
        {
            if (File.Exists(_lastAutoWeightFile))
            {
                string content = File.ReadAllText(_lastAutoWeightFile);
                if (double.TryParse(content, out double weight))
                {
                    return weight;
                }
            }
            return 0;
        }
        /// <summary>
        /// 记录上次称重Id到本地文件
        /// </summary>
        /// <param name="weight"></param>
        private static void WriteLastId(string id)
        {
            _lastId = id;
            File.WriteAllText(_lastIdFile, id);
        }
        /// <summary>
        /// 从本地读取上次称重Id
        /// </summary>
        /// <returns></returns>
        private static string ReadLastId()
        {
            if (File.Exists(_lastIdFile))
            {
                string content = File.ReadAllText(_lastIdFile);
                return content;
            }
            return "";
        }

        private void AutoSave()
        {
            // 0.查询最后一次称重记录
            string sql = $@"
SELECT TOP 1 Id,WeighingWeightBegin,WeighingTimeBegin,WeighingWeightEnd,WeighingTimeEnd 
FROM [dbo].[WeighingRecord] 
WHERE MachineId='{this.cmbConvertMachine.Text}'
ORDER BY WeighingTimeBegin DESC";
            DataTable dt = SA.GetDataTable(sql, Utils.GetParameterValue("DBConnStr"));

            // 1.第一次自动称重
            if (dt?.Rows?.Count == 0)
            {
                AutoInsert(); // 如果没有记录，直接插入新记录
                WriteLastAutoWeight(Convert.ToDouble(_currentWeight));
                return;
            }

            // 2.没有结束时间
            // 开始时间不能超过当前1天
            // 本次重量大于上次重量
            // 则更新上次记录的结束重量和时间，并插入新记录
            if (!DateTime.TryParse(dt?.Rows?[0]?["WeighingTimeEnd"]?.ToString(), out _))
            {
                DateTime? startTime = Convert.ToDateTime(dt?.Rows[0]?["WeighingTimeBegin"]?.ToString()); // 开始时间
                if (DateTime.Now - startTime <= TimeSpan.FromDays(1))
                {
                    double currentWeight = Convert.ToDouble(_currentWeight);
                    double lastWeight = ReadLastAutoWeight();
                    if (currentWeight - lastWeight > 0.5) // 防止抖动
                    {
                        // 如果大于上次重量，则更新最后一次称重记录
                        AutoUpdate(lastWeight);
                        AutoInsert();
                    }
                    else
                    {
                        return; // 本次重量没有增加，不处理（防止 WriteLastAutoWeight）
                    }
                }
                else
                { // 超过1天，更新上次的称重记录，并直接插入新记录
                    double lastWeight = ReadLastAutoWeight();
                    AutoUpdate(lastWeight);
                    AutoInsert();
                }
            }
            else
            { // 有结束时间，直接插入新记录
                AutoInsert();
            }
            // 记录上次自动称重重量，用于下次比较
            WriteLastAutoWeight(Convert.ToDouble(_currentWeight));
        }
        #endregion

        private void AutoInsert()
        {
            string insertSql = string.Format(@"
INSERT INTO WeighingRecord 
(Plant,MachineId,Shift,WeighingType,WaterRate,WeighingWeightBegin,WeighingTimeBegin,Site) 
VALUES ('{0}','{1}','{2}','{3}','{4}','{5}','{6}','{7}'); 
SELECT SCOPE_IDENTITY();"
                    , this.cmbPlant.Text
                    , this.cmbConvertMachine.Text
                    , (this.cmbShift.SelectedItem as ComboBoxItem).Value
                    , _currentWeightType
                    , this.numWaterRate.Value
                    , Convert.ToDecimal(_currentWeight)
                    , DateTime.Now
                    , this.cmbSite.Text);
            Log("SqlRecords", insertSql);
            string addRes = SA.ExecuteScalar(insertSql, Utils.GetParameterValue("DBConnStr")).ToString();
            if (int.TryParse(addRes, out int newId))
            {
                //_lastId = newId; // 设置最后一次新增记录的ID
                WriteLastId(newId.ToString());
            }
            UpdateLblStatus($"状态: 记录已新增", Color.Green);
            Log($"{addRes}记录已新增: 开始称重重量 {_currentWeight}{_currentUnit}");
        }
        private void AutoUpdate(double? weight = null)
        {
            if (weight == null)
            {
                weight = Convert.ToDouble(_currentWeight);
            }
            string updateSql = string.Format("UPDATE WeighingRecord SET WeighingWeightEnd='{0}',WeighingTimeEnd='{1}',WaterRate='{2}' WHERE Id='{3}'"
                    , weight
                    , DateTime.Now
                    , this.numWaterRate.Value
                    , _lastId);
            Log("SqlRecords", updateSql);
            int upRes = SA.ExecuteNonQuery(updateSql, Utils.GetParameterValue("DBConnStr"));
            UpdateLblStatus($"状态: 记录已更新", Color.Green);
            Log($"{_lastId}记录已更新: 结束称重重量 {_currentWeight}{_currentUnit}");
            _lastId = null;
        }

        private void SaveToDGV_old()
        {
            if (string.IsNullOrEmpty(_currentWeight) || _currentWeight.Trim() == "0.000")
            {
                MessageBox.Show("当前重量为零，无需保存！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_isBeginWeighing) // 开始称重 新增
            {
                string insertSql = string.Format(@"
INSERT INTO WeighingRecord 
(Plant,MachineId,Shift,WeighingType,WaterRate,WeighingWeightBegin,WeighingTimeBegin,Site) 
VALUES ('{0}','{1}','{2}','{3}','{4}','{5}','{6}','{7}'); 
SELECT SCOPE_IDENTITY();"
                    , this.cmbPlant.Text
                    , this.cmbConvertMachine.Text
                    , (this.cmbShift.SelectedItem as ComboBoxItem).Value
                    , _currentWeightType
                    , this.numWaterRate.Value
                    , Convert.ToDecimal(_currentWeight)
                    , DateTime.Now
                    , this.cmbSite.Text);
                string addRes = SA.ExecuteScalar(insertSql, Utils.GetParameterValue("DBConnStr")).ToString();
                if (int.TryParse(addRes, out int newId))
                {
                    //_lastId = newId;
                    WriteLastId(newId.ToString());
                }
                UpdateLblStatus($"状态: 记录已新增", Color.Green);
                Log($"{addRes}记录已新增: 开始称重重量 {_currentWeight}{_currentUnit}");
            }
            else // 结束称重 修改
            {
                string updateSql = string.Format("UPDATE WeighingRecord SET WeighingWeightEnd='{0}',WeighingTimeEnd='{1}',WaterRate='{2}' WHERE Id='{3}'"
                    , Convert.ToDecimal(_currentWeight)
                    , DateTime.Now
                    , this.numWaterRate.Value
                    , _lastId);
                int upRes = SA.ExecuteNonQuery(updateSql, Utils.GetParameterValue("DBConnStr"));
                UpdateLblStatus($"状态: 记录已更新", Color.Green);
                Log($"{_lastId}记录已更新: 结束称重重量 {_currentWeight}{_currentUnit}");
                _lastId = null;
            }
        }
        private void SaveToDGV()
        {
            if (string.IsNullOrEmpty(_currentWeight) || _currentWeight.Trim() == "0.000")
            {
                MessageBox.Show("当前重量为零，无需保存！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_isBeginWeighing) // 开始称重 新增
            {
                AutoInsert();
            }
            else // 结束称重 修改
            {
                AutoUpdate();
            }
        }

        /// <summary>
        /// 直接设置第一行的高亮样式
        /// </summary>
        private void HighlightFirstRow()
        {
            // 关键判断：确保DataGridView有数据行，避免索引越界
            if (dgvRecords.Rows.Count > 0 && !dgvRecords.Rows[0].IsNewRow)
            {
                // 设置行的背景色（高亮核心）
                dgvRecords.Rows[0].DefaultCellStyle.BackColor = Color.LightSkyBlue;
                // 可选：设置前景色（文字颜色），提升对比度
                dgvRecords.Rows[0].DefaultCellStyle.ForeColor = Color.DarkBlue;
                // 可选：设置字体加粗，强化高亮效果
                dgvRecords.Rows[0].DefaultCellStyle.Font = new Font(dgvRecords.Font, FontStyle.Bold);
            }
        }

        private void UpdateLblStatus(string content, Color color)
        {
            lblStatus.Text = content;
            lblStatus.ForeColor = color;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            DisconnectFromScale();
        }

        private static void Log(string data)
        {
            Utils.AppendToFile(Utils.SystemLogFile, data, true);
        }
        private static void Log(string logPath, string data)
        {
            Utils.AppendToFile(logPath, data, true);
        }

    }

    public class ComboBoxItem
    {
        public string Text { get; set; }
        public string Value { get; set; }
        public ComboBoxItem(string text, string value)
        {
            Text = text;
            Value = value;
        }
        public override string ToString()
        {
            return Text;
        }
    }

}