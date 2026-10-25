namespace ParkingDataServer
{
    public class Devices
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int DeviceType { get; set; }
        public string Location { get; set; }
    }

    public class ParkingSlots
    {
        public string Id { get; set; }
        public string SlotName { get; set; }
        public string SensorId { get; set; }
        public int Status { get; set; }
        public string CurrentPlate { get; set; }
        public string UpdatedAt { get; set; }
    }

    public class ParkingLogs
    {
        public int Id { get; set; }
        public string LicensePlate { get; set; }
        public string GateInId { get; set; }
        public string TimeIn { get; set; }
        public string TimeOut { get; set; }
        public int Fee { get; set; }
        public int Status { get; set; }
    }

    // Cấu trúc gói tin MQTT nhận từ Tầng Edge (giống mẫu { id, value } của giảng viên)
    public class MqttPayload
    {
        public string id { get; set; }
        public PayloadData value { get; set; }
    }

    public class PayloadData
    {
        public string type { get; set; }      // "SENSOR" hoặc "LPR"
        public string slot_id { get; set; }   // Dùng cho SENSOR (VD: "SLOT_A1")
        public int is_occupied { get; set; }  // 0: Trống, 1: Có xe
        public string gate { get; set; }      // "IN" hoặc "OUT"
        public string plate { get; set; }     // Biển số LPR (VD: "30A-888.99")
    }
}