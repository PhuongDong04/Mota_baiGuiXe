using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace ParkingClientApp
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient httpClient = new HttpClient();
        private MqttClient mqttClient;
        private const string API_BASE = "http://localhost:8080/api";

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) =>
            {
                await LoadDataFromServerApi();
                ConnectRealtimeNotification();
            };
        }

        // Lắng nghe thông báo từ Server để tự động làm mới UI ngay khi có xe vào/ra
        private void ConnectRealtimeNotification()
        {
            try
            {
                mqttClient = new MqttClient("broker.hivemq.com");
                mqttClient.MqttMsgPublishReceived += (s, e) =>
                {
                    Dispatcher.Invoke(async () => await LoadDataFromServerApi());
                };
                mqttClient.Connect("ClientUI_" + Guid.NewGuid().ToString().Substring(0, 5));
                mqttClient.Subscribe(new string[] { "parking/ui/update" }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
            }
            catch { /* Nếu lỗi mạng vẫn có thể bấm nút Làm mới qua API */ }
        }

        // GỌI REST API XUỐNG TẦNG 2 (SERVER LAYER) ĐỂ LẤY DỮ LIỆU
        private async Task LoadDataFromServerApi()
        {
            try
            {
                string slotsJson = await httpClient.GetStringAsync($"{API_BASE}/slots");
                var slots = JsonConvert.DeserializeObject<List<SlotViewModel>>(slotsJson);

                string logsJson = await httpClient.GetStringAsync($"{API_BASE}/logs");
                var logs = JsonConvert.DeserializeObject<List<LogViewModel>>(logsJson);

                icSlots.ItemsSource = slots;
                dgLogs.ItemsSource = logs;

                int emptyCount = slots.Count(x => x.Status == 0);
                int activeCars = logs.Count(x => x.Status == 0);
                lblSummary.Text = $"Trống: {emptyCount}/{slots.Count} ô | Xe đang trong bãi: {activeCars} chiếc";
            }
            catch
            {
                lblSummary.Text = "Chưa kết nối được với ParkingDataServer (http://localhost:8080)";
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadDataFromServerApi();
        }
    }

    public class SlotViewModel
    {
        public string Id { get; set; }
        public string SlotName { get; set; }
        public string SensorId { get; set; }
        public int Status { get; set; }
        public string UpdatedAt { get; set; }

        // THÊM MỚI: Thuộc tính hứng dữ liệu từ API
        public string CurrentPlate { get; set; }

        // THÊM MỚI: Logic hiển thị (Trống thì hiện dấu gạch, Có xe thì hiện Biển số)
        public string PlateDisplay => Status == 1 ? CurrentPlate : "---";
        public string StatusText => Status == 1 ? "CÓ XE ĐỖ" : "CÒN TRỐNG";
        public string ColorHex => Status == 1 ? "#EF4444" : "#10B981"; // Đỏ / Xanh lá
    }

    public class LogViewModel
    {
        public int Id { get; set; }
        public string LicensePlate { get; set; }
        public string TimeIn { get; set; }
        public string TimeOut { get; set; }
        public int Fee { get; set; }
        public int Status { get; set; }
        public string StatusDisplay => Status == 0 ? "Đang gửi" : "Đã lấy xe";
    }
}