using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json;
using uPLibrary.Networking.M2Mqtt;

namespace DeviceSimulator
{
    public partial class MainWindow : Window
    {
        private MqttClient mqttClient;
        private DispatcherTimer autoTimer;
        private Random rand = new Random();
        private const string TOPIC = "parking/device/status";

        private int autoStep = 0;     // Biến theo dõi đang ở bước thứ mấy
        private string autoPlate = "";// Lưu biển số xe đang chạy tự động

        public MainWindow()
        {
            InitializeComponent();
            autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            autoTimer.Tick += AutoTimer_Tick;
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                mqttClient = new MqttClient(txtBroker.Text.Trim());
                string clientId = "EdgeSim_" + Guid.NewGuid().ToString().Substring(0, 5);
                mqttClient.Connect(clientId);

                if (mqttClient.IsConnected)
                {
                    AppendLog($"[CONNECTED] Đã kết nối tới {txtBroker.Text}:1883 (ClientId: {clientId})");
                    btnConnect.Content = "Đã kết nối MQTT";
                    btnConnect.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi kết nối MQTT: " + ex.Message);
            }
        }

        private void PublishPayload(object payloadObj)
        {
            if (mqttClient == null || !mqttClient.IsConnected)
            {
                MessageBox.Show("Vui lòng nhấn 'Kết nối MQTT Broker' trước!");
                return;
            }

            string json = JsonConvert.SerializeObject(payloadObj);
            mqttClient.Publish(TOPIC, Encoding.UTF8.GetBytes(json));
            AppendLog($"[PUBLISH {DateTime.Now:HH:mm:ss}]\n{json}\n");
        }

        // Gửi tín hiệu Cảm biến Siêu âm/Từ trường
        private void SendSensorState(int isOccupied)
        {
            var selectedItem = (ComboBoxItem)cboSlots.SelectedItem;
            string slotId = selectedItem.Content.ToString();
            string sensorId = selectedItem.Tag.ToString();

            var payload = new
            {
                id = sensorId,
                value = new
                {
                    type = "SENSOR",
                    slot_id = slotId,
                    is_occupied = isOccupied
                }
            };
            PublishPayload(payload);
        }

        private void BtnSensorOccupied_Click(object sender, RoutedEventArgs e) => SendSensorState(1);
        private void BtnSensorEmpty_Click(object sender, RoutedEventArgs e) => SendSensorState(0);

        // Sinh biển số xe ngẫu nhiên
        private void BtnRandomPlate_Click(object sender, RoutedEventArgs e)
        {
            string[] prefixes = { "29A", "30E", "30H", "30K", "15A", "51G", "43A" };
            txtPlate.Text = $"{prefixes[rand.Next(prefixes.Length)]}-{rand.Next(100, 999)}.{rand.Next(10, 99)}";
        }

        // Gửi tín hiệu Camera LPR Cổng Vào / Ra
        private void SendLprEvent(string gateType)
        {
            var payload = new
            {
                id = gateType == "IN" ? "CAM_IN_01" : "CAM_OUT_01",
                value = new
                {
                    type = "LPR",
                    gate = gateType,
                    plate = txtPlate.Text.Trim()
                }
            };
            PublishPayload(payload);
        }

        private void BtnGateIn_Click(object sender, RoutedEventArgs e) => SendLprEvent("IN");
        private void BtnGateOut_Click(object sender, RoutedEventArgs e) => SendLprEvent("OUT");

        // Chế độ tự động gửi định kỳ 5 giây/lần
        private void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            if (!autoTimer.IsEnabled)
            {
                autoTimer.Start();
                btnAuto.Content = "Đang gửi tự động... (Nhấn để Dừng)";
                AppendLog("--- BẮT ĐẦU CHẾ ĐỘ GIẢ LẬP TỰ ĐỘNG (5s/lần) ---");
            }
            else
            {
                autoTimer.Stop();
                btnAuto.Content = "Bắt đầu gửi tự động (5 giây/lần)";
                AppendLog("--- ĐÃ DỪNG CHẾ ĐỘ TỰ ĐỘNG ---");
            }
        }

        private void AutoTimer_Tick(object sender, EventArgs e)
        {
            switch (autoStep)
            {
                case 0: // BƯỚC 1: CAMERA VÀO CỔNG
                    BtnRandomPlate_Click(null, null); // Sinh biển ngẫu nhiên
                    autoPlate = txtPlate.Text;        // Nhớ biển số này lại
                    SendLprEvent("IN");               // Bắn tín hiệu VÀO
                    AppendLog($"--- [AUTO 1/4] Xe {autoPlate} đi VÀO cổng ---");
                    autoStep = 1; // Chuyển sang bước 2
                    break;

                case 1: // BƯỚC 2: CẢM BIẾN BÁO CÓ XE ĐỖ
                    cboSlots.SelectedIndex = rand.Next(0, cboSlots.Items.Count); // Chọn chuồng ngẫu nhiên
                    SendSensorState(1);               // Bắn tín hiệu CÓ XE
                    AppendLog($"--- [AUTO 2/4] Xe {autoPlate} đã lùi vào chuồng ---");
                    autoStep = 2; // Chuyển sang bước 3
                    break;

                case 2: // BƯỚC 3: CẢM BIẾN BÁO TRỐNG
                    SendSensorState(0);               // Bắn tín hiệu TRỐNG
                    AppendLog($"--- [AUTO 3/4] Xe {autoPlate} rời chuồng, trả lại chỗ Trống ---");
                    autoStep = 3; // Chuyển sang bước 4
                    break;

                case 3: // BƯỚC 4: CAMERA RA CỔNG
                    txtPlate.Text = autoPlate;        // Gắn lại đúng cái biển số lúc vào
                    SendLprEvent("OUT");              // Bắn tín hiệu RA
                    AppendLog($"--- [AUTO 4/4] Xe {autoPlate} đi RA cổng (Kết thúc chu trình) ---");
                    autoStep = 0; // Quay vòng lại Bước 1 cho xe khác
                    break;
            }
        }

        private void AppendLog(string msg)
        {
            txtLog.AppendText(msg + Environment.NewLine);
            txtLog.ScrollToEnd();
        }
    }
}