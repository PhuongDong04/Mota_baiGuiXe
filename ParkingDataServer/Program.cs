using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace ParkingDataServer
{
    class Program
    {
        static MqttClient mqttClient;
        //const string BROKER_URL = "broker.emqx.io";
        const string BROKER_URL = "broker.hivemq.com"; // Đổi từ broker.emqx.io sang HiveMQ

        const string TOPIC_SUB = "parking/device/status";
        const string TOPIC_PUB_UI = "parking/ui/update";

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "TẦNG 2: PARKING DATA SERVER (MQTT + SQL + API)";
            Console.WriteLine("=== KHỞI ĐỘNG HỆ THỐNG SERVER BÃI ĐỖ XE THÔNG MINH ===");

            // 1. Khởi tạo Provider.cs và quét cấu trúc Database (Migrate)
            var provider = new Provider();
            Provider.ConnectionError += (connStr) => Console.WriteLine($"[LỖI DB] Không thể kết nối: {connStr}");
            Provider.CommandError += (sql) => Console.WriteLine($"[LỖI SQL] {sql}");

            Console.WriteLine("[1] Đang đồng bộ cấu trúc bảng từ SQL Server (Migrate)...");
            Provider.Current.Schema.Migrate();
            Console.WriteLine($"    -> Đã nạp thành công {Provider.Current.Schema.Tables.Count} bảng từ DB '{provider.DatabaseName}'.");

            // 2. Khởi chạy Web API (HttpListener) ở luồng nền phục vụ Tầng 3 (App Layer)
            Task.Run(() => StartRestApiServer());

            // 3. Kết nối tới MQTT Broker
            Console.WriteLine($"[2] Đang kết nối tới MQTT Broker ({BROKER_URL}:1883)...");
            try
            {
                mqttClient = new MqttClient(BROKER_URL);
                mqttClient.MqttMsgPublishReceived += OnMqttMessageReceived;

                string clientId = "ParkingServer_" + Guid.NewGuid().ToString().Substring(0, 6);

                // Dòng 44 cũ nằm ở đây, giờ đã được bọc an toàn
                mqttClient.Connect(clientId);

                if (mqttClient.IsConnected)
                {
                    mqttClient.Subscribe(new string[] { TOPIC_SUB }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
                    Console.WriteLine($"    -> Đã kết nối MQTT! Đang lắng nghe topic: '{TOPIC_SUB}'\n");
                }
            }
            catch (uPLibrary.Networking.M2Mqtt.Exceptions.MqttCommunicationException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[LỖI MQTT] Không thể kết nối tới Broker '{BROKER_URL}'.");
                Console.WriteLine($"Nguyên nhân: Mạng chập chờn, Broker đang bảo trì hoặc Firewall chặn Port 1883.");
                Console.WriteLine($"Chi tiết lỗi: {ex.Message}");
                Console.ResetColor();
            }
        }

        // XỬ LÝ BẢN TIN MQTT TỪ TẦNG EDGE GỬI LÊN
        private static void OnMqttMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            string json = Encoding.UTF8.GetString(e.Message);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[MQTT NHẬN] {DateTime.Now:HH:mm:ss} | Payload: {json}");
            Console.ResetColor();

            try
            {
                var payload = JsonConvert.DeserializeObject<MqttPayload>(json);
                if (payload == null || payload.value == null) return;

                // Tra cứu thiết bị trong bảng Devices bằng Provider.cs (giống bài mẫu của thầy)
                var deviceTable = Provider.GetTable<Devices>();
                var dev = deviceTable.Find<Devices>(payload.id);
                string devName = dev != null ? dev.Name : "Thiết bị chưa đăng ký";
                Console.WriteLine($"   -> Thiết bị phát: [{payload.id}] - {devName}");

                string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // TRƯỜNG HỢP 1: CẢM BIẾN SIÊU ÂM / TỪ TRƯỜNG BÁO TRẠNG THÁI Ô ĐỖ
                if (payload.value.type == "SENSOR")
                {
                    var slotTable = Provider.GetTable<ParkingSlots>();
                    var slot = slotTable.Find<ParkingSlots>(payload.value.slot_id);

                    // Gọi thêm bảng Lịch sử (ParkingLogs) ra để đối chiếu logic
                    var logTable = Provider.GetTable<ParkingLogs>();

                    if (slot != null)
                    {
                        if (payload.value.is_occupied == 1) // CẢM BIẾN BÁO CÓ XE
                        {
                            // 1. Chặn đứng hành vi đỗ đè lên ô đã có xe
                            if (slot.Status == 1)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"   -> [LỖI LOGIC] Ô {slot.SlotName} ĐÃ CÓ XE ({slot.CurrentPlate}), không thể đỗ thêm! Tín hiệu bị từ chối.");
                                Console.ResetColor();
                                return;
                            }

                            // 2. Lấy danh sách TOÀN BỘ xe đang trong bãi (Đã qua cổng IN nhưng chưa qua cổng OUT)
                            var carsInLot = logTable.ToList<ParkingLogs>(null, "Status = 0");

                            // 3. Lấy danh sách các ô đỗ HIỆN ĐANG CÓ XE để trích xuất các biển số đã yên vị
                            var occupiedSlots = slotTable.ToList<ParkingSlots>(null, "Status = 1");
                            var parkedPlates = occupiedSlots.Select(s => s.CurrentPlate).ToList();

                            // 4. BỘ LỌC QUAN TRỌNG: Tìm các xe đang trong bãi NHƯNG chưa nằm trong bất kỳ ô đỗ nào
                            var unassignedCars = carsInLot.Where(c => !parkedPlates.Contains(c.LicensePlate)).ToList();

                            if (unassignedCars.Count > 0)
                            {
                                // Có xe đang rảnh -> Gán ngẫu nhiên 1 xe vào ô này
                                slot.Status = 1;
                                slot.UpdatedAt = nowStr;

                                Random rnd = new Random();
                                slot.CurrentPlate = unassignedCars[rnd.Next(unassignedCars.Count)].LicensePlate;

                                string updateSql = slotTable.CreateUpdateSql(slot);
                                Provider.CreateCommand(cmd => { cmd.CommandText = updateSql; cmd.ExecuteNonQuery(); });

                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"   -> [HỢP LỆ] Xe '{slot.CurrentPlate}' (vừa chạy tìm chỗ) đã đậu vào {slot.SlotName}. Đã bôi Đỏ.");
                                Console.ResetColor();
                            }
                            else
                            {
                                // Bãi có xe qua cổng, nhưng TẤT CẢ xe đó đều đã vào chuồng hết rồi. 
                                // Cảm biến tự nhiên báo có xe -> Tín hiệu Rác / Dị vật
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"   -> [CẢNH BÁO BẢO MẬT] Cảm biến {slot.SlotName} báo CÓ XE nhưng không có xe nào đang rảnh để đỗ! Tín hiệu bị từ chối.");
                                Console.ResetColor();
                                return;
                            }
                        }
                        else // CẢM BIẾN YÊU CẦU BÁO TRỐNG (ĐỔI MÀU XANH)
                        {
                            string oldPlate = slot.CurrentPlate; // Lưu lại biển số xe vừa rời đi để in log

                            slot.Status = 0;
                            slot.CurrentPlate = "";
                            slot.UpdatedAt = nowStr;

                            string updateSql = slotTable.CreateUpdateSql(slot);
                            Provider.CreateCommand(cmd => {
                                cmd.CommandText = updateSql; cmd.ExecuteNonQuery();
                            });

                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"   -> [HỢP LỆ] Xe '{(string.IsNullOrEmpty(oldPlate) ? "Ẩn danh" : oldPlate)}' đã rời {slot.SlotName}, trả lại chỗ Trống (Xanh).");
                            Console.ResetColor();
                        }
                    }
                }
                // TRƯỜNG HỢP 2: CAMERA LPR NHẬN DIỆN BIỂN SỐ TẠI CỔNG VÀO / RA
                else if (payload.value.type == "LPR")
                {
                    var logTable = Provider.GetTable<ParkingLogs>();
                    string plate = payload.value.plate;

                    if (payload.value.gate == "IN")
                    {
                        var newLog = new ParkingLogs
                        {
                            LicensePlate = plate,
                            GateInId = payload.id,
                            TimeIn = nowStr,
                            TimeOut = null,
                            Fee = 0,
                            Status = 0 // 0 = Đang gửi trong bãi
                        };

                        string insertSql = logTable.CreateInsertSql(newLog);
                        Provider.CreateCommand(cmd => {
                            cmd.CommandText = insertSql;
                            cmd.ExecuteNonQuery();
                        });

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"   -> [LPR CỔNG VÀO] Mở barie cho xe '{plate}' vào bãi lúc {nowStr}.");
                        Console.ResetColor();
                    }
                    else if (payload.value.gate == "OUT")
                    {
                        // =====================================================================
                        // BỘ LỌC MỚI: KIỂM TRA XE ĐÃ THỰC SỰ RỜI KHỎI CHUỒNG HAY CHƯA?
                        // =====================================================================
                        var slotTable = Provider.GetTable<ParkingSlots>();

                        // Tìm xem biển số chuẩn bị ra cổng này có đang bị dính ở bất kỳ ô đỗ nào (Status = 1) không?
                        var stillParkedSlots = slotTable.ToList<ParkingSlots>(null, $"Status = 1 AND CurrentPlate = '{plate}'");

                        if (stillParkedSlots.Count > 0)
                        {
                            // Xe vẫn đang ở trong chuồng -> Từ chối mở barie cổng ra!
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"   -> [CẢNH BÁO GIAN LẬN] Xe '{plate}' vẫn đang báo đỗ tại {stillParkedSlots[0].SlotName}. Vui lòng lùi xe khỏi chuồng (Báo Trống) trước khi qua Cổng Ra!");
                            Console.ResetColor();
                            return; // Dừng xử lý ngay lập tức, không tính tiền, không cho ra.
                        }
                        // =====================================================================

                        // NẾU XE ĐÃ RỜI CHUỒNG (HỢP LỆ), TIẾP TỤC LOGIC TÍNH TIỀN CŨ...
                        var activeLogs = logTable.ToList<ParkingLogs>(null, $"LicensePlate = '{plate}' AND Status = 0");
                        if (activeLogs.Count > 0)
                        {
                            var log = activeLogs[0];

                            DateTime timeIn = DateTime.Parse(log.TimeIn);
                            DateTime timeOut = DateTime.Parse(nowStr);
                            TimeSpan parkedTime = timeOut - timeIn;

                            double blocks = Math.Ceiling(parkedTime.TotalMinutes / 30.0);
                            if (blocks == 0) blocks = 1;

                            log.TimeOut = nowStr;
                            log.Fee = (int)blocks * 15000;
                            log.Status = 1;

                            string updateSql = logTable.CreateUpdateSql(log);
                            Provider.CreateCommand(cmd => {
                                cmd.CommandText = updateSql;
                                cmd.ExecuteNonQuery();
                            });

                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.WriteLine($"   -> [LPR CỔNG RA] Xe '{plate}' rời bãi. Phí thu: {log.Fee:N0} VNĐ.");
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.WriteLine($"   -> [CẢNH BÁO LPR] Không tìm thấy dữ liệu xe '{plate}' trong bãi!");
                        }
                    }
                }

                // Báo cho Tầng 3 (App UI) biết dữ liệu vừa thay đổi để làm mới giao diện
                mqttClient.Publish(TOPIC_PUB_UI, Encoding.UTF8.GetBytes("REFRESH"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LỖI XỬ LÝ] {ex.Message}");
            }
        }

        // TÍCH HỢP WEB API NHỎ GỌN PHỤC VỤ TẦNG 3 (APPLICATION LAYER)
        private static void StartRestApiServer()
        {
            try
            {
                HttpListener listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:8080/api/");
                listener.Start();
                Console.WriteLine("[3] Web API đang lắng nghe tại: http://localhost:8080/api/ (slots | logs)");

                while (true)
                {
                    HttpListenerContext context = listener.GetContext();
                    string path = context.Request.Url.AbsolutePath.ToLower();
                    string responseJson = "{}";

                    if (path.EndsWith("/slots"))
                    {
                        var slots = Provider.Select<ParkingSlots>();
                        responseJson = JsonConvert.SerializeObject(slots);
                    }
                    else if (path.EndsWith("/logs"))
                    {
                        var logs = Provider.Select<ParkingLogs>().OrderByDescending(x => x.Id).Take(30).ToList();
                        responseJson = JsonConvert.SerializeObject(logs);
                    }

                    byte[] buffer = Encoding.UTF8.GetBytes(responseJson);
                    context.Response.ContentType = "application/json; charset=utf-8";
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    context.Response.OutputStream.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LỖI API] {ex.Message}");
            }
        }
    }
}