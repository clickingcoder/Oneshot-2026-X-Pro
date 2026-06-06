// =============================================================================
//  Oneshot FVG Strategy – Quantower v1.145.16
//  Version: Oneshot 2026 V2.3 Tele — adds Telegram alerts (entry Long/Short, order size, stop trigger, take-profit reached, profit/loss in $) on top of V2.2 reconnect-recovery (re-resolve live symbol on disconnect, identity-based matching, fast watchdog) and V2.1 (late-fill race fix; reversal mode OFF=Wick / ON=Body; FVG touch accepts b0 OR b1)
//  Approach: Pure polling — no AddSymbol, no OnBar, no NewHistoryItem
//  Trail logic ported from TradeManager v1.17 (ProcessTick / MoveStopAsync pattern)
// -----------------------------------------------------------------------------
//  [VI] Chiến lược Oneshot FVG cho Quantower.
//  [VI] Phiên bản 1.20 (Oneshot V1.20): Tự tính số contract theo số tiền rủi ro + cắt bớt
//       contract sau khi khớp lệnh để giữ rủi ro $ không vượt mức cài đặt;
//       báo cáo Lãi/Lỗ thực hiện mỗi lệnh (song ngữ) từ các fill đóng.
//  [VI] Cách chạy: Thuần "polling" (hỏi vòng) — KHÔNG dùng AddSymbol/OnBar.
//  [VI] Logic trailing được port từ TradeManager v1.17.
// -----------------------------------------------------------------------------
//  [DE] Oneshot-FVG-Strategie für Quantower.
//  [DE] Version 1.20 (Oneshot V1.20): Berechnet die Kontraktanzahl automatisch nach Risikobetrag
//       und schneidet nach der Ausführung überschüssige Kontrakte ab, damit das
//       $-Risiko den eingestellten Wert nie überschreitet; meldet den realisierten
//       Gewinn/Verlust pro Trade (mehrsprachig) aus den schließenden Fills.
//  [DE] Ausführung: Reines "Polling" (Abfrageschleife) — KEIN AddSymbol/OnBar.
//  [DE] Trailing-Logik portiert aus TradeManager v1.17.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;

public class OneshotFVGStrategy : Strategy
{
    // =========================================================================
    //  INPUT PARAMETERS
    //  These are user-configurable settings exposed in the Quantower UI.
    //  Each [InputParameter] attribute defines the label, sort order, and
    //  allowed range shown to the user when setting up the strategy.
    // -------------------------------------------------------------------------
    //  [VI] THAM SỐ ĐẦU VÀO
    //  [VI] Đây là các cài đặt người dùng chỉnh được trên giao diện Quantower.
    //  [VI] Mỗi [InputParameter] định nghĩa nhãn hiển thị, thứ tự sắp xếp và
    //       khoảng giá trị cho phép khi cài đặt chiến lược.
    // -------------------------------------------------------------------------
    //  [DE] EINGABEPARAMETER
    //  [DE] Dies sind die vom Benutzer einstellbaren Optionen in der Quantower-Oberfläche.
    //  [DE] Jedes [InputParameter] definiert die Beschriftung, die Sortierreihenfolge
    //       und den zulässigen Wertebereich beim Einrichten der Strategie.
    // =========================================================================

    // The trading instrument (e.g., ES, NQ) to run the strategy on.
    // [VI] Công cụ giao dịch (vd: ES, NQ, MNQ) để chạy chiến lược.
    // [DE] Das Handelsinstrument (z. B. ES, NQ, MNQ), auf dem die Strategie läuft.
    [InputParameter("Symbol", 0)]
    public Symbol CurrentSymbol { get; set; }

    // The brokerage account to use for placing orders.
    // [VI] Tài khoản môi giới dùng để đặt lệnh.
    // [DE] Das Broker-Konto, über das Orders platziert werden.
    [InputParameter("Account", 1)]
    public Account CurrentAccount { get; set; }

    // The chart timeframe used for FVG detection and EMA calculation.
    // [VI] Khung thời gian dùng để phát hiện FVG và tính EMA.
    // [DE] Der Chart-Zeitrahmen für die FVG-Erkennung und EMA-Berechnung.
    [InputParameter("Timeframe", 2)]
    public Period CurrentPeriod { get; set; } = Period.MIN5;

    // Number of bars used to calculate the EMA trend filter.
    // [VI] Số nến dùng để tính đường EMA lọc xu hướng.
    // [DE] Anzahl der Bars zur Berechnung des EMA-Trendfilters.
    [InputParameter("EMA Trend Length", 10, 1, 1000, 1, 0)]
    public int EmaPeriod = 200;

    // How many bars back to look for a valid FVG touch (zone expiry window).
    // [VI] Nhìn lại bao nhiêu nến để tìm chạm FVG hợp lệ (cửa sổ hết hạn của vùng).
    // [DE] Wie viele Bars zurück nach einer gültigen FVG-Berührung gesucht wird (Ablauffenster der Zone).
    [InputParameter("Bar Lookback Length", 20, 1, 500, 1, 0)]
    public int BarLookback = 25;

    // Fixed number of contracts to trade when dynamic sizing is OFF.
    // [VI] Số contract cố định khi TẮT chế độ tính size động.
    // [DE] Feste Kontraktanzahl, wenn die dynamische Größenberechnung AUS ist.
    [InputParameter("Contracts Per Trade", 30, 1, 100, 1, 0)]
    public int NumContracts = 2;

    // Sizing mode switch: false = use fixed NumContracts above; true = auto-size by dollar risk.
    // [VI] Công tắc chế độ size: false = dùng NumContracts cố định; true = tự tính theo $ rủi ro.
    // [DE] Größenmodus-Schalter: false = feste NumContracts oben; true = automatisch nach Dollar-Risiko.
    [InputParameter("Use Risk Amount (ON=$Risk / OFF=Fix Contracts)", 31)]
    public bool UseRiskAmount = false;

    // Dollar amount to risk per trade when UseRiskAmount is ON.
    // Bot will calculate: contracts = floor(RiskAmountUSD / (slPoints × pointValue)).
    // Minimum 1 contract. Default = $300.
    // [VI] Số tiền (USD) rủi ro mỗi lệnh khi bật UseRiskAmount.
    // [VI] Bot tính: số contract = làm tròn xuống( RiskAmountUSD / (điểm SL × giá trị 1 điểm) ).
    // [VI] Tối thiểu 1 contract. Mặc định $300.
    // [DE] Dollarbetrag, der pro Trade riskiert wird, wenn UseRiskAmount AN ist.
    // [DE] Der Bot berechnet: Kontrakte = abrunden( RiskAmountUSD / (SL-Punkte × Punktwert) ).
    // [DE] Minimum 1 Kontrakt. Standard $300.
    [InputParameter("Risk Amount Per Trade ($)", 32, 1.0, 100000.0, 50.0, 0)]
    public double RiskAmountUSD = 300.0;

    // Risk-to-Reward ratio: TP distance = SL distance × this multiplier.
    // [VI] Tỉ lệ Lời:Lỗ — khoảng cách TP = khoảng cách SL × hệ số này.
    // [DE] Chance-Risiko-Verhältnis: TP-Abstand = SL-Abstand × dieser Faktor.
    [InputParameter("Take Profit Multiplier (R:R)", 40, 0.1, 10.0, 0.1, 1)]
    public double TpMultiplier = 4.0;

    // Maximum allowed stop loss distance in points. Signals with wider SL are skipped.
    // [VI] Khoảng cách SL tối đa cho phép (điểm). Tín hiệu có SL rộng hơn sẽ bị bỏ qua.
    // [DE] Maximal zulässiger Stop-Loss-Abstand in Punkten. Signale mit weiterem SL werden übersprungen.
    [InputParameter("Max Stop Loss (Points)", 50, 1.0, 1000.0, 1.0, 1)]
    public double MaxSLPoints = 75.0;

    // If true, trades with SL wider than MaxSLPoints are still taken using a fixed cap instead.
    // [VI] Nếu true, lệnh có SL rộng hơn MaxSLPoints vẫn được vào nhưng dùng mức SL trần cố định.
    // [DE] Wenn true, werden Trades mit SL weiter als MaxSLPoints dennoch mit einer festen Obergrenze ausgeführt.
    [InputParameter("Allow Trade With Capped SL", 60)]
    public bool UseCappedSL = true;

    // The fixed SL distance (in points) to use when the signal SL is too wide and capping is enabled.
    // [VI] Khoảng cách SL cố định (điểm) dùng khi SL tín hiệu quá rộng và bật chế độ trần SL.
    // [DE] Fester SL-Abstand (in Punkten), wenn der Signal-SL zu weit ist und die Deckelung aktiviert ist.
    [InputParameter("Capped SL (Points)", 70, 1.0, 1000.0, 1.0, 1)]
    public double CappedSLPoints = 75.0;

    // If true, the bot can flip from Long→Short or Short→Long on a new opposite signal.
    // [VI] Nếu true, bot có thể đảo chiều Long→Short hoặc Short→Long khi gặp tín hiệu ngược.
    // [DE] Wenn true, kann der Bot bei einem neuen Gegensignal von Long→Short oder Short→Long wechseln.
    [InputParameter("Allow Reverse on Opposite Signal", 80)]
    public bool AllowReverse = false;

    // Reversal candle confirmation style (toggle).
    //   OFF = Wick Break (original): b0 must close beyond b1's full wick (high/low).
    //         Long  needs c0 > h1 ; Short needs c0 < l1.
    //   ON  = Body Break: b0 must close beyond b1's body open (o1), i.e. body-engulfing.
    //         Long  needs c0 > o1 ; Short needs c0 < o1.
    // The FVG-touch (b0 OR b1) behaviour applies in BOTH modes — it is not affected by this toggle.
    // [VI] Kiểu xác nhận nến đảo chiều (công tắc).
    // [VI]   OFF = Wick Break (gốc): b0 phải đóng vượt RÂU nến b1 (đỉnh/đáy).
    // [VI]         Long cần c0 > h1 ; Short cần c0 < l1.
    // [VI]   ON  = Body Break: b0 phải đóng vượt giá MỞ thân nến b1 (o1) — kiểu nuốt thân.
    // [VI]         Long cần c0 > o1 ; Short cần c0 < o1.
    // [VI] Việc chạm FVG bằng b0 HOẶC b1 áp dụng cho CẢ HAI chế độ — không phụ thuộc công tắc này.
    // [DE] Stil der Umkehrkerzen-Bestätigung (Schalter).
    // [DE]   OFF = Wick Break (Original): b0 muss über die volle Lunte von b1 schließen (High/Low).
    // [DE]         Long braucht c0 > h1 ; Short braucht c0 < l1.
    // [DE]   ON  = Body Break: b0 muss über die Körper-Eröffnung von b1 (o1) schließen — Body-Engulfing.
    // [DE]         Long braucht c0 > o1 ; Short braucht c0 < o1.
    // [DE] Die FVG-Berührung (b0 ODER b1) gilt in BEIDEN Modi — von diesem Schalter unbeeinflusst.
    [InputParameter("Reversal Mode (OFF=Wick Break / ON=Body Break)", 88)]
    public bool UseBodyReversal = false;

    // Throttle: maximum number of trade entries allowed within any 60-second window.
    // [VI] Giới hạn: số lệnh vào tối đa trong mỗi cửa sổ 60 giây.
    // [DE] Drossel: maximale Anzahl an Einstiegen innerhalb eines beliebigen 60-Sekunden-Fensters.
    [InputParameter("Max Trades Per Minute", 85, 1, 100, 1, 0)]
    public int MaxTradesPerMinute = 10;

    // How many milliseconds to wait after an entry order before checking for the fill.
    // This gives the broker time to confirm the position before we try to place SL/TP.
    // [VI] Chờ bao nhiêu mili-giây sau khi gửi lệnh vào trước khi kiểm tra khớp.
    // [VI] Cho sàn thời gian xác nhận vị thế trước khi bot đặt SL/TP.
    // [DE] Wie viele Millisekunden nach der Einstiegsorder gewartet wird, bevor die Ausführung geprüft wird.
    // [DE] Gibt dem Broker Zeit, die Position zu bestätigen, bevor SL/TP gesetzt werden.
    [InputParameter("Fill Debounce (ms)", 95, 100, 2000, 50, 0)]
    public int DebounceMs = 450;

    // When ON, the bot enters on EVERY newly-closed bar, including catch-up bars that
    // arrive together in a burst. Turn this ON when running Quantower in "Speed control"/
    // replay so a signal landing on a replayed (non-newest) bar is NOT skipped — this is
    // what makes the bot match the indicator, which marks every signal. When OFF (live),
    // only the most recent bar may enter, so a reconnect/data burst never fires a late
    // entry on a bar that already closed minutes ago. Leave OFF for real-time trading.
    // [VI] Khi BẬT, bot vào lệnh trên MỌI nến vừa đóng, kể cả các nến bù về dồn một lúc.
    // [VI] BẬT khi chạy Quantower ở chế độ "Speed control"/replay để tín hiệu rơi vào nến
    // [VI] replay (không phải nến mới nhất) KHÔNG bị bỏ — nhờ vậy bot khớp indicator vốn
    // [VI] đánh dấu mọi tín hiệu. Khi TẮT (chạy thật), chỉ nến mới nhất được vào lệnh, để
    // [VI] feed nạp dồn/kết nối lại không bắn lệnh trễ trên nến đã đóng mấy phút trước. Chạy
    // [VI] thật thì để TẮT.
    // [DE] Wenn AN, tritt der Bot bei JEDEM neu geschlossenen Bar ein, auch bei Nachhol-Bars,
    // [DE] die im Bündel ankommen. AN für Quantower "Speed control"/Replay, damit ein Signal
    // [DE] auf einem Replay-Bar (nicht dem jüngsten) NICHT übersprungen wird — so passt der
    // [DE] Bot zum Indikator, der jedes Signal markiert. AUS (live): nur der jüngste Bar darf
    // [DE] eintreten. Für Echtzeithandel AUS lassen.
    [InputParameter("Replay / Backtest Mode (enter on every bar)", 96)]
    public bool ReplayMode = false;

    // If true, the strategy only signals trades during the defined session window.
    // [VI] Nếu true, chiến lược chỉ phát tín hiệu trong khung phiên đã định.
    // [DE] Wenn true, erzeugt die Strategie nur innerhalb des definierten Sitzungsfensters Signale.
    [InputParameter("Use Trading Hours Filter", 100)]
    public bool UseTradingHours = false;

    // Session start time (hour component, in the session's local timezone).
    // [VI] Giờ bắt đầu phiên (theo múi giờ địa phương của phiên).
    // [DE] Sitzungsbeginn (Stunde, in der lokalen Zeitzone der Sitzung).
    [InputParameter("Session Start — Hour (0-23)", 101, 0, 23, 1, 0)]
    public int SessionStartHour = 2;

    // Session start time (minute component).
    // [VI] Phút bắt đầu phiên.
    // [DE] Sitzungsbeginn (Minute).
    [InputParameter("Session Start — Minute (0-59)", 102, 0, 59, 1, 0)]
    public int SessionStartMinute = 0;

    // Session end time (hour component). Set both end fields to 00:00 for "no end time".
    // [VI] Giờ kết thúc phiên. Đặt cả giờ và phút kết thúc = 00:00 nghĩa là "không có giờ kết thúc".
    // [DE] Sitzungsende (Stunde). Beide Endfelder auf 00:00 setzen bedeutet "kein Ende".
    [InputParameter("Session End — Hour (0-23)", 103, 0, 23, 1, 0)]
    public int SessionEndHour = 0;

    // Session end time (minute component).
    // [VI] Phút kết thúc phiên.
    // [DE] Sitzungsende (Minute).
    [InputParameter("Session End — Minute (0-59)", 104, 0, 59, 1, 0)]
    public int SessionEndMinute = 0;

    // The UTC offset of the session timezone (e.g. -5 for US Eastern, +7 for Vietnam ICT).
    // [VI] Độ lệch UTC của múi giờ phiên (vd: -5 cho giờ Mỹ Đông, +7 cho giờ Việt Nam).
    // [DE] UTC-Versatz der Sitzungszeitzone (z. B. -5 für US-Ostküste, +7 für Vietnam ICT).
    [InputParameter("Session Timezone — UTC Offset (hours)", 105, -12, 14, 1, 0)]
    public int SessionUTCOffset = 0;

    // =========================================================================
    //  TRAILING STOP PARAMETERS
    //  Controls the behaviour of the dynamic trailing stop feature.
    // -------------------------------------------------------------------------
    //  [VI] THAM SỐ TRAILING STOP (DỜI SL THEO GIÁ)
    //  [VI] Điều khiển hành vi của tính năng dời stop-loss động.
    // -------------------------------------------------------------------------
    //  [DE] TRAILING-STOP-PARAMETER
    //  [DE] Steuern das Verhalten der dynamischen Trailing-Stop-Funktion.
    // =========================================================================

    // Master switch: set to true to enable the trailing stop feature.
    // [VI] Công tắc tổng: đặt true để bật tính năng trailing stop.
    // [DE] Hauptschalter: auf true setzen, um die Trailing-Stop-Funktion zu aktivieren.
    [InputParameter("— TRAILING STOP —", 109)]
    public bool UseTrailingStop = false;

    // Minimum profit (in points) the trade must reach before the trail activates.
    // The stop will not move until price has moved at least this far in your favour.
    // [VI] Mức lãi tối thiểu (điểm) lệnh phải đạt trước khi trail kích hoạt.
    // [VI] Stop sẽ không dời cho đến khi giá đi đúng hướng ít nhất khoảng này.
    // [DE] Mindestgewinn (in Punkten), den der Trade erreichen muss, bevor das Trailing aktiviert wird.
    // [DE] Der Stop bewegt sich nicht, bis der Kurs mindestens so weit zu Ihren Gunsten gelaufen ist.
    [InputParameter("Trail Trigger — Profit Points (vd: 15)", 110, 0.25, 1000.0, 0.25, 2)]
    public double TrailTriggerPoints = 15.0;

    // How many points behind the current price the trailing stop will be placed.
    // [VI] Trailing stop được đặt cách giá hiện tại bao nhiêu điểm (phía sau).
    // [DE] Wie viele Punkte hinter dem aktuellen Kurs der Trailing-Stop platziert wird.
    [InputParameter("Trail Offset — Points behind price (vd: 15)", 111, 0.25, 1000.0, 0.25, 2)]
    public double TrailOffsetPoints = 15.0;

    // Minimum distance (in points) the new stop must improve over the previous stop
    // before a stop-move order is sent. Prevents excessive micro-adjustments.
    // [VI] Khoảng cải thiện tối thiểu (điểm) mà stop mới phải tốt hơn stop cũ
    // [VI] trước khi gửi lệnh dời stop. Tránh điều chỉnh vụn vặt quá nhiều.
    // [DE] Mindestabstand (in Punkten), um den der neue Stop den vorherigen verbessern muss,
    // [DE] bevor eine Stop-Verschiebung gesendet wird. Verhindert übermäßige Mikro-Anpassungen.
    [InputParameter("Trail Min Move — Points (vd: 1)", 112, 0.25, 1000.0, 0.25, 2)]
    public double TrailMinMovePoints = 1.0;

    // Minimum seconds between consecutive stop-move operations (throttle).
    // [VI] Số giây tối thiểu giữa hai lần dời stop liên tiếp (giới hạn tần suất).
    // [DE] Mindestsekunden zwischen aufeinanderfolgenden Stop-Verschiebungen (Drossel).
    [InputParameter("Trail Cooldown — Seconds (vd: 2)", 113, 0, 60, 1, 0)]
    public double TrailCooldownSeconds = 2.0;

    // How long (ms) to wait for the old SL cancel to be confirmed before giving up.
    // [VI] Chờ bao lâu (ms) để xác nhận hủy SL cũ trước khi bỏ cuộc.
    // [DE] Wie lange (ms) auf die Bestätigung der Stornierung des alten SL gewartet wird, bevor abgebrochen wird.
    [InputParameter("Trail Cancel Verify Timeout (ms)", 114, 50, 2000, 50, 0)]
    public int TrailCancelVerifyMs = 500;

    // =========================================================================
    //  TELEGRAM ALERT PARAMETERS
    //  Sends a push message to a Telegram bot on entry (Long/Short + size +
    //  stop trigger + take profit) and on close (take-profit reached / stop
    //  triggered + realised profit or loss in dollars).
    // -------------------------------------------------------------------------
    //  [VI] THAM SỐ CẢNH BÁO TELEGRAM
    //  [VI] Gửi tin nhắn tới bot Telegram khi vào lệnh (Long/Short + size +
    //       stop trigger + take profit) và khi đóng lệnh (chạm TP / dính stop
    //       + Lãi/Lỗ thực hiện theo đô-la).
    // -------------------------------------------------------------------------
    //  [DE] TELEGRAM-BENACHRICHTIGUNGSPARAMETER
    //  [DE] Sendet bei Einstieg und Ausstieg eine Push-Nachricht an einen Telegram-Bot.
    // =========================================================================

    // Master switch: turn the Telegram alert feature ON/OFF.
    // [VI] Công tắc tổng: BẬT/TẮT tính năng cảnh báo Telegram.
    // [DE] Hauptschalter: Telegram-Benachrichtigungen EIN/AUS.
    [InputParameter("— TELEGRAM ALERTS — (ON/OFF)", 115)]
    public bool UseTelegramAlerts = false;

    // Your Telegram bot token (from @BotFather), e.g. 123456789:ABCdef...
    // [VI] Token bot Telegram của bạn (lấy từ @BotFather), vd: 123456789:ABCdef...
    // [DE] Ihr Telegram-Bot-Token (von @BotFather).
    [InputParameter("Telegram Bot Token", 116)]
    public string TelegramBotToken = "";

    // The chat/channel ID the bot sends messages to (get it from @userinfobot
    // or the getUpdates API). Required in addition to the token.
    // [VI] ID chat/kênh mà bot gửi tin tới (lấy từ @userinfobot hoặc API getUpdates).
    // [VI] Bắt buộc phải có cùng với token.
    // [DE] Die Chat-/Kanal-ID, an die der Bot Nachrichten sendet.
    [InputParameter("Telegram Chat ID", 117)]
    public string TelegramChatId = "";

    // =========================================================================
    //  PRIVATE BACKING FIELD FOR COMPILER SAFETY
    //  A local copy of TpMultiplier stored at strategy start so it cannot
    //  change mid-trade if the user edits the input parameter while running.
    // -------------------------------------------------------------------------
    //  [VI] BIẾN NỘI BỘ ĐỂ AN TOÀN
    //  [VI] Bản sao cục bộ của TpMultiplier lưu lúc khởi động chiến lược, để nó
    //       không bị đổi giữa lệnh nếu người dùng sửa tham số khi đang chạy.
    // -------------------------------------------------------------------------
    //  [DE] PRIVATES SICHERUNGSFELD FÜR COMPILER-SICHERHEIT
    //  [DE] Eine lokale Kopie von TpMultiplier, beim Start der Strategie gespeichert,
    //       damit sie sich nicht mitten im Trade ändert, falls der Benutzer den
    //       Parameter während des Laufs bearbeitet.
    // =========================================================================
    private double tpMultiplierInternal = 0.5;

    // =========================================================================
    //  PRIVATE STATE — FEED / EMA / FEED STALENESS
    //  Variables managing the historical bar data feed and the EMA indicator.
    // -------------------------------------------------------------------------
    //  [VI] TRẠNG THÁI NỘI BỘ — NGUỒN DỮ LIỆU / EMA / KIỂM TRA NGUỒN CŨ
    //  [VI] Các biến quản lý nguồn dữ liệu nến lịch sử và chỉ báo EMA.
    // -------------------------------------------------------------------------
    //  [DE] PRIVATER ZUSTAND — DATEN-FEED / EMA / FEED-VERALTUNG
    //  [DE] Variablen zur Verwaltung des historischen Bar-Daten-Feeds und des EMA-Indikators.
    // =========================================================================
    // The historical data subscription that supplies OHLC bars for signal detection.
    // [VI] Đăng ký dữ liệu lịch sử cung cấp nến OHLC để phát hiện tín hiệu.
    // [DE] Das Historische-Daten-Abonnement, das OHLC-Bars für die Signalerkennung liefert.
    private HistoricalData _hdm;
    // The EMA indicator instance attached to the historical data feed.
    // [VI] Thực thể chỉ báo EMA gắn vào nguồn dữ liệu lịch sử.
    // [DE] Die EMA-Indikator-Instanz, die an den Historische-Daten-Feed angehängt ist.
    private Indicator _ema;
    // Timestamp of the last bar we received from the feed (used for staleness detection).
    // [VI] Mốc thời gian nến cuối nhận từ nguồn (dùng để phát hiện nguồn bị treo/cũ).
    // [DE] Zeitstempel des letzten vom Feed empfangenen Bars (zur Erkennung von Veraltung).
    private DateTime _lastHdmBarTime = DateTime.MinValue;
    // How many seconds without a new bar before the watchdog considers the feed stale.
    // [VI] Bao nhiêu giây không có nến mới thì watchdog coi nguồn là cũ/treo.
    // [DE] Wie viele Sekunden ohne neuen Bar, bevor der Watchdog den Feed als veraltet einstuft.
    private const int FeedStaleSec = 1800;

    // Identity of the configured instrument, captured at OnRun. After a
    // disconnect/reconnect Quantower disposes the old Symbol object and builds a
    // fresh one, so the CurrentSymbol reference goes stale (and Quantower may even
    // reassign the input to a same-named symbol on another connection). We
    // re-resolve the live symbol by these identity fields, pinned to the original
    // broker connection — never a same-named instrument from another data feed.
    // [VI] Danh tính instrument đã cấu hình, lưu lúc OnRun. Sau khi mất kết nối/
    // [VI] kết nối lại, Quantower hủy object Symbol cũ và tạo cái mới, nên con trỏ
    // [VI] CurrentSymbol bị "chết" (Quantower thậm chí có thể gán input sang symbol
    // [VI] cùng tên ở kết nối khác). Ta dùng các trường này để tìm lại symbol live,
    // [VI] ghim đúng connection broker — không vớ nhầm symbol cùng tên ở nguồn khác.
    private string _symbolId;
    private string _symbolName;
    private string _connectionId;
    private string _accountId;

    // =========================================================================
    //  PRIVATE STATE — POLLING
    //  Variables that drive the recurring timer-based bar-check loop.
    // -------------------------------------------------------------------------
    //  [VI] TRẠNG THÁI NỘI BỘ — POLLING (HỎI VÒNG)
    //  [VI] Các biến điều khiển vòng lặp kiểm tra nến định kỳ bằng Timer.
    // -------------------------------------------------------------------------
    //  [DE] PRIVATER ZUSTAND — POLLING (ABFRAGESCHLEIFE)
    //  [DE] Variablen, die die wiederkehrende Timer-basierte Bar-Prüfschleife steuern.
    // =========================================================================
    // The recurring Timer that fires OnPoll() every PollIntervalMs milliseconds.
    // [VI] Timer lặp lại gọi OnPoll() mỗi PollIntervalMs mili-giây.
    // [DE] Der wiederkehrende Timer, der OnPoll() alle PollIntervalMs Millisekunden auslöst.
    private Timer _mainPoller = null;
    // Timestamp of the last bar that has already been processed (to skip duplicates).
    // [VI] Mốc thời gian nến đã xử lý gần nhất (để bỏ qua nến trùng).
    // [DE] Zeitstempel des zuletzt bereits verarbeiteten Bars (um Duplikate zu überspringen).
    private DateTime _lastProcessedBar = DateTime.MinValue;
    // Wall-clock time when we last observed a new bar (used by the watchdog heartbeat).
    // [VI] Thời gian thực khi thấy nến mới gần nhất (dùng cho nhịp tim của watchdog).
    // [DE] Echtzeit, zu der zuletzt ein neuer Bar beobachtet wurde (vom Watchdog-Herzschlag genutzt).
    private DateTime _lastBarTime = DateTime.MinValue;
    // How often (ms) the polling timer fires to check for a new completed bar.
    // [VI] Tần suất (ms) Timer polling chạy để kiểm tra nến mới đã đóng.
    // [DE] Wie oft (ms) der Polling-Timer auslöst, um auf einen neuen abgeschlossenen Bar zu prüfen.
    private const int PollIntervalMs = 1000;

    // =========================================================================
    //  PRIVATE STATE — WARMUP
    //  Flags and counters used during the initial historical bar scan that
    //  populates the FVG zone lists and primes the EMA before live trading.
    // -------------------------------------------------------------------------
    //  [VI] TRẠNG THÁI NỘI BỘ — WARMUP (KHỞI ĐỘNG/LÀM NÓNG)
    //  [VI] Cờ và bộ đếm dùng trong lần quét nến lịch sử ban đầu để dựng danh
    //       sách vùng FVG và mồi EMA trước khi giao dịch thật.
    // -------------------------------------------------------------------------
    //  [DE] PRIVATER ZUSTAND — WARMUP (AUFWÄRMEN)
    //  [DE] Flags und Zähler für den anfänglichen Scan historischer Bars, der die
    //       FVG-Zonenlisten füllt und den EMA vor dem Live-Handel vorbereitet.
    // =========================================================================
    // True once the warmup scan has completed successfully and live trading can begin.
    // [VI] True khi quét warmup xong và có thể bắt đầu giao dịch thật.
    // [DE] True, sobald der Warmup-Scan erfolgreich abgeschlossen ist und der Live-Handel beginnen kann.
    private bool _warmedUp = false;
    // True once the warmup scan has been attempted (prevents running it more than once).
    // [VI] True khi đã thử quét warmup (tránh chạy lại nhiều lần).
    // [DE] True, sobald der Warmup-Scan versucht wurde (verhindert mehrfaches Ausführen).
    private bool _warmupAttempted = false;
    // Rolling bar counter incremented every time ProcessBar() is called.
    // [VI] Bộ đếm nến, tăng mỗi lần gọi ProcessBar().
    // [DE] Fortlaufender Bar-Zähler, der bei jedem Aufruf von ProcessBar() erhöht wird.
    private int _barCount = 0;
    // True if the most recent bar closed above the EMA (uptrend); false = downtrend.
    // [VI] True nếu nến gần nhất đóng trên EMA (tăng); false = giảm.
    // [DE] True, wenn der jüngste Bar über dem EMA geschlossen hat (Aufwärtstrend); false = Abwärtstrend.
    private bool _isUptrend = false;
    // Human-readable description of the last signal, shown in the strategy metrics panel.
    // [VI] Mô tả dễ đọc của tín hiệu cuối, hiển thị ở bảng chỉ số chiến lược.
    // [DE] Lesbare Beschreibung des letzten Signals, angezeigt im Strategie-Metrik-Panel.
    private string _lastSignal = "Waiting for history...";
    // The EMA value from the most recently processed bar (NaN until the first valid bar).
    // [VI] Giá trị EMA của nến xử lý gần nhất (NaN cho đến khi có nến hợp lệ đầu tiên).
    // [DE] Der EMA-Wert des zuletzt verarbeiteten Bars (NaN bis zum ersten gültigen Bar).
    private double _lastEmaVal = double.NaN;

    // =========================================================================
    //  PRIVATE STATE — FVG ZONES
    //  Parallel lists that store all currently active Fair-Value Gap zones.
    //  Each zone is identified by its top price, bottom price, and the bar
    //  number on which it was formed.
    // -------------------------------------------------------------------------
    //  [VI] TRẠNG THÁI NỘI BỘ — CÁC VÙNG FVG
    //  [VI] Các danh sách song song lưu mọi vùng Fair-Value Gap đang hoạt động.
    //  [VI] Mỗi vùng được nhận diện bằng giá đỉnh, giá đáy và số thứ tự nến tạo ra nó.
    // -------------------------------------------------------------------------
    //  [DE] PRIVATER ZUSTAND — FVG-ZONEN
    //  [DE] Parallele Listen, die alle aktuell aktiven Fair-Value-Gap-Zonen speichern.
    //  [DE] Jede Zone wird durch ihren oberen Preis, unteren Preis und die Bar-Nummer
    //       ihrer Entstehung identifiziert.
    // =========================================================================
    // Bullish FVG zone top prices (lower edge of the gap candle's low).
    // [VI] Giá đỉnh vùng FVG tăng.
    // [DE] Obere Preise der bullischen FVG-Zonen (untere Kante des Tiefs der Gap-Kerze).
    private readonly List<double> _bullTop = new List<double>();
    // Bullish FVG zone bottom prices (upper edge of the two-bars-ago candle's high).
    // [VI] Giá đáy vùng FVG tăng.
    // [DE] Untere Preise der bullischen FVG-Zonen (obere Kante des Hochs der Kerze von vor zwei Bars).
    private readonly List<double> _bullBtm = new List<double>();
    // Bar numbers on which each bullish FVG was created (used for expiry).
    // [VI] Số thứ tự nến tạo ra mỗi FVG tăng (dùng cho hết hạn).
    // [DE] Bar-Nummern, an denen jede bullische FVG entstand (für den Ablauf genutzt).
    private readonly List<int> _bullBar = new List<int>();
    // Bearish FVG zone top prices (lower edge of the two-bars-ago candle's low).
    // [VI] Giá đỉnh vùng FVG giảm.
    // [DE] Obere Preise der bärischen FVG-Zonen (untere Kante des Tiefs der Kerze von vor zwei Bars).
    private readonly List<double> _bearTop = new List<double>();
    // Bearish FVG zone bottom prices (upper edge of the gap candle's high).
    // [VI] Giá đáy vùng FVG giảm.
    // [DE] Untere Preise der bärischen FVG-Zonen (obere Kante des Hochs der Gap-Kerze).
    private readonly List<double> _bearBtm = new List<double>();
    // Bar numbers on which each bearish FVG was created (used for expiry).
    // [VI] Số thứ tự nến tạo ra mỗi FVG giảm (dùng cho hết hạn).
    // [DE] Bar-Nummern, an denen jede bärische FVG entstand (für den Ablauf genutzt).
    private readonly List<int> _bearBar = new List<int>();

    // --- Retrace-into-FVG observation (early "watch" logging) ---
    // [VI] --- Theo dõi giá retrace về vùng FVG (log cảnh báo sớm để quan sát) ---
    // [DE] --- Retrace-in-FVG-Beobachtung (frühe "Watch"-Protokollierung) ---
    // Bar number of the last Bull FVG zone we already logged a retrace for (anti-spam dedup).
    // [VI] Số nến của vùng Bull FVG gần nhất ĐÃ ghi log retrace (chống lặp/spam).
    // [DE] Bar-Nummer der zuletzt für einen Retrace protokollierten Bull-FVG-Zone (Anti-Spam).
    private int _lastRetraceBullZone = -1;
    // Bar number of the last Bear FVG zone we already logged a retrace for (anti-spam dedup).
    // [VI] Số nến của vùng Bear FVG gần nhất ĐÃ ghi log retrace (chống lặp/spam).
    // [DE] Bar-Nummer der zuletzt für einen Retrace protokollierten Bear-FVG-Zone (Anti-Spam).
    private int _lastRetraceBearZone = -1;

    // =========================================================================
    //  PRIVATE STATE — TRADE / ORDER MANAGEMENT
    //  All flags and prices that describe the current open trade lifecycle.
    // -------------------------------------------------------------------------
    //  [VI] TRẠNG THÁI NỘI BỘ — QUẢN LÝ LỆNH / VỊ THẾ
    //  [VI] Tất cả cờ và mức giá mô tả vòng đời lệnh đang mở hiện tại.
    // -------------------------------------------------------------------------
    //  [DE] PRIVATER ZUSTAND — TRADE-/ORDER-VERWALTUNG
    //  [DE] Alle Flags und Preise, die den Lebenszyklus des aktuellen offenen Trades beschreiben.
    // =========================================================================
    // True while a trade is considered open (from signal detection through position close).
    // [VI] True khi đang có lệnh mở (từ lúc phát tín hiệu đến khi đóng vị thế).
    // [DE] True, solange ein Trade als offen gilt (von der Signalerkennung bis zum Schließen der Position).
    private bool _inTrade = false;
    // True if the current trade is a Long; false if Short.
    // [VI] True nếu lệnh hiện tại là Long; false nếu Short.
    // [DE] True, wenn der aktuelle Trade ein Long ist; false bei Short.
    private bool _isLong = false;
    // True while a flatten (market-close) operation is in progress.
    // [VI] True khi đang thực hiện đóng toàn bộ vị thế (flatten) bằng lệnh thị trường.
    // [DE] True, während eine Flatten-Operation (Markt-Schließung) im Gange ist.
    private bool _flattening = false;
    // The actual average fill price of the entry order.
    // [VI] Giá khớp trung bình thực tế của lệnh vào.
    // [DE] Der tatsächliche durchschnittliche Ausführungspreis der Einstiegsorder.
    private double _entryPrice = 0;
    // Current stop-loss price. Updated when the trailing stop moves.
    // [VI] Giá stop-loss hiện tại. Được cập nhật khi trailing stop dời.
    // [DE] Aktueller Stop-Loss-Preis. Wird aktualisiert, wenn der Trailing-Stop sich bewegt.
    private double _slPrice = 0;
    // Current take-profit price.
    // [VI] Giá take-profit hiện tại.
    // [DE] Aktueller Take-Profit-Preis.
    private double _tpPrice = 0;
    // The distance (in points) between entry and stop-loss.
    // [VI] Khoảng cách (điểm) giữa giá vào và stop-loss.
    // [DE] Der Abstand (in Punkten) zwischen Einstieg und Stop-Loss.
    private double _slDist = 0;
    // The total filled quantity (contracts) of the current entry.
    // [VI] Tổng số lượng đã khớp (contract) của lệnh vào hiện tại.
    // [DE] Die gesamte ausgeführte Menge (Kontrakte) des aktuellen Einstiegs.
    private double _fillQty = 0;
    // True once both the SL and TP orders have been successfully placed post-fill.
    // [VI] True khi cả lệnh SL và TP đã được đặt thành công sau khi khớp.
    // [DE] True, sobald sowohl SL- als auch TP-Order nach der Ausführung erfolgreich platziert wurden.
    private bool _slTPSet = false;
    // Order ID of the currently active stop-loss order (null if none placed yet).
    // [VI] ID lệnh stop-loss đang hoạt động (null nếu chưa đặt).
    // [DE] Order-ID der aktuell aktiven Stop-Loss-Order (null, wenn noch keine platziert).
    private string _activeSLId = null;
    // Order ID of the currently active take-profit order (null if none placed yet).
    // [VI] ID lệnh take-profit đang hoạt động (null nếu chưa đặt).
    // [DE] Order-ID der aktuell aktiven Take-Profit-Order (null, wenn noch keine platziert).
    private string _activeTPId = null;

    // The raw signal SL price from the bar candle (low of signal bar for Long,
    // high for Short). Saved at signal time and used after fill to compute exact SL distance.
    // [VI] Giá SL thô từ nến tín hiệu (đáy nến cho Long, đỉnh nến cho Short).
    // [VI] Lưu lúc có tín hiệu, dùng sau khi khớp để tính chính xác khoảng cách SL.
    // [DE] Der rohe Signal-SL-Preis aus der Bar-Kerze (Tief des Signal-Bars für Long,
    // [DE] Hoch für Short). Beim Signal gespeichert und nach Ausführung zur genauen SL-Abstandsberechnung genutzt.
    private double _signalRawSLPrice = double.NaN;

    // --- Realized P/L accumulators for the current trade (filled via OnTradeAdded) ---
    // [VI] --- Bộ cộng dồn Lãi/Lỗ thực hiện cho lệnh hiện tại (điền qua OnTradeAdded) ---
    // [DE] --- Akkumulatoren für realisierten G/V des aktuellen Trades (gefüllt über OnTradeAdded) ---
    // Sum of realized NET P/L (after fees) from this trade's closing fills.
    // [VI] Tổng Lãi/Lỗ RÒNG (sau phí) thực hiện từ các fill đóng của lệnh này.
    // [DE] Summe des realisierten NETTO-G/V (nach Gebühren) aus den schließenden Fills dieses Trades.
    private double _tradeRealizedNet = 0.0;
    // Sum of realized GROSS P/L from this trade's closing fills.
    // [VI] Tổng Lãi/Lỗ GỘP thực hiện từ các fill đóng của lệnh này.
    // [DE] Summe des realisierten BRUTTO-G/V aus den schließenden Fills dieses Trades.
    private double _tradeRealizedGross = 0.0;
    // Price of the last closing fill (shown as the exit price in the log).
    // [VI] Giá của fill đóng cuối cùng (hiển thị làm giá ra trong log).
    // [DE] Preis des letzten schließenden Fills (im Log als Ausstiegspreis angezeigt).
    private double _tradeExitPrice = 0.0;
    // True once at least one closing fill has been seen for this trade.
    // [VI] True khi đã thấy ít nhất một fill đóng cho lệnh này.
    // [DE] True, sobald mindestens ein schließender Fill für diesen Trade erfasst wurde.
    private bool _tradeClosingSeen = false;

    // One-shot Timer that fires after DebounceMs to read the confirmed fill price.
    // [VI] Timer chạy một lần sau DebounceMs để đọc giá khớp đã xác nhận.
    // [DE] Einmal-Timer, der nach DebounceMs auslöst, um den bestätigten Ausführungspreis zu lesen.
    private Timer _debounceTimer = null;
    // True from the moment the entry order is sent until the debounce timer fires.
    // [VI] True từ lúc gửi lệnh vào cho đến khi timer debounce kích hoạt.
    // [DE] True vom Senden der Einstiegsorder bis zum Auslösen des Debounce-Timers.
    private bool _pendingFill = false;
    // How many times OnDebounceFlush has re-armed waiting for the broker to confirm
    // the fill. The broker can confirm the position LATER than DebounceMs, so instead
    // of resetting immediately (which makes the late-arriving position look "unmanaged"
    // and get flattened), we retry up to MaxDebounceRetries before giving up.
    // [VI] Số lần OnDebounceFlush đã chờ lại để sàn xác nhận khớp. Sàn có thể xác nhận
    // [VI] vị thế MUỘN hơn DebounceMs, nên thay vì reset ngay (khiến vị thế về trễ bị
    // [VI] coi là "lạ" và bị flatten), ta thử lại tối đa MaxDebounceRetries lần rồi mới bỏ.
    private int _debounceRetries = 0;
    // Maximum number of extra DebounceMs waits before declaring the entry truly unfilled.
    // [VI] Số lần chờ thêm (mỗi lần DebounceMs) tối đa trước khi kết luận lệnh thật sự chưa khớp.
    private const int MaxDebounceRetries = 8;
    // Mutex lock used to protect shared state accessed from multiple threads
    // (the polling timer, the tick event, and the debounce timer all run concurrently).
    // [VI] Khóa (mutex) bảo vệ trạng thái dùng chung được truy cập từ nhiều luồng
    // [VI] (timer polling, sự kiện tick và timer debounce chạy đồng thời).
    // [DE] Mutex-Sperre zum Schutz des gemeinsamen Zustands, auf den mehrere Threads zugreifen
    // [DE] (Polling-Timer, Tick-Ereignis und Debounce-Timer laufen gleichzeitig).
    private readonly object _lock = new object();

    // Sliding window of timestamps for every trade sent in the last 60 seconds
    // (used to enforce MaxTradesPerMinute rate limiting).
    // [VI] Cửa sổ trượt chứa mốc thời gian mọi lệnh gửi trong 60 giây qua
    // [VI] (dùng để áp giới hạn MaxTradesPerMinute).
    // [DE] Gleitendes Fenster der Zeitstempel jedes in den letzten 60 Sekunden gesendeten Trades
    // [DE] (zur Durchsetzung der MaxTradesPerMinute-Begrenzung).
    private readonly Queue<DateTime> _tradeTimestamps = new Queue<DateTime>();

    // --- Reverse trade pending state ---
    // [VI] --- Trạng thái chờ của lệnh đảo chiều ---
    // True when the bot has decided to flip direction and is waiting for the
    // current position to close before placing the new opposite entry.
    // [VI] True khi bot quyết định đảo chiều và đang chờ vị thế hiện tại đóng
    // [VI] trước khi đặt lệnh ngược mới.
    private bool _pendingReverse = false;
    // Direction of the pending reverse entry.
    // [VI] Hướng của lệnh đảo chiều đang chờ.
    private bool _reverseIsLong = false;
    // SL price pre-calculated for the reverse entry.
    // [VI] Giá SL tính sẵn cho lệnh đảo chiều.
    private double _reverseSLPrice = 0;
    // TP price pre-calculated for the reverse entry.
    // [VI] Giá TP tính sẵn cho lệnh đảo chiều.
    private double _reverseTPPrice = 0;
    // SL distance (points) for the reverse entry (used for dynamic sizing).
    // [VI] Khoảng cách SL (điểm) cho lệnh đảo chiều (dùng cho tính size động).
    private double _reverseSLDist = 0;
    // Number of contracts for the reverse entry.
    // [VI] Số contract cho lệnh đảo chiều.
    private int _reverseContracts = 0;

    // =========================================================================
    //  PRIVATE STATE — TRAILING STOP
    //  Runtime state for the tick-by-tick trailing stop logic.
    // -------------------------------------------------------------------------
    //  [VI] TRẠNG THÁI NỘI BỘ — TRAILING STOP
    //  [VI] Trạng thái lúc chạy của logic trailing stop theo từng tick.
    // =========================================================================
    // True once the trail has been "unlocked" (profit ≥ TrailTriggerPoints reached).
    // [VI] True khi trail đã được "mở khóa" (đạt lãi ≥ TrailTriggerPoints).
    private bool _trailActivated = false;
    // True while the strategy is subscribed to the symbol's NewLast tick event.
    // [VI] True khi chiến lược đang đăng ký sự kiện tick NewLast của symbol.
    private bool _trailSubscribed = false;
    // Guard flag: true while a stop-move async operation is already in flight
    // (prevents overlapping concurrent stop moves).
    // [VI] Cờ canh: true khi đang có thao tác dời stop bất đồng bộ chạy dở
    // [VI] (tránh nhiều lần dời stop chồng chéo nhau).
    private bool _isMovingStop = false;
    // UTC time of the last successful stop-move (enforces TrailCooldownSeconds).
    // [VI] Thời gian UTC lần dời stop thành công gần nhất (áp TrailCooldownSeconds).
    private DateTime _lastStopMoveTime = DateTime.MinValue;
    // Price of the last tick that was processed (used to filter sub-tick noise).
    // [VI] Giá của tick xử lý gần nhất (dùng để lọc nhiễu nhỏ hơn 1 tick).
    private double _lastTickPrice = double.NaN;
    // Total number of times the trailing stop has been moved this trade (informational).
    // [VI] Tổng số lần trailing stop đã dời trong lệnh này (chỉ để tham khảo).
    private int _trailMoveCount = 0;

    // =========================================================================
    //  PRIVATE STATE — WATCHDOG
    //  A background timer that periodically logs a heartbeat and detects
    //  if the market data feed has gone stale, then reconnects it.
    // -------------------------------------------------------------------------
    //  [VI] TRẠNG THÁI NỘI BỘ — WATCHDOG (CANH GÁC)
    //  [VI] Timer nền định kỳ ghi nhịp tim và phát hiện nếu nguồn dữ liệu bị
    //       treo/cũ, sau đó kết nối lại.
    // =========================================================================
    private Timer _watchdogTimer = null;
    // How often (ms) the watchdog fires: every 30 seconds.
    // [VI] Tần suất (ms) watchdog chạy: mỗi 30 giây.
    private const int WatchdogIntervalMs = 30_000;

    // =========================================================================
    //  CONSTRUCTOR
    //  Sets the human-readable name and description shown in Quantower's
    //  strategy list. Called once when the strategy is loaded.
    // -------------------------------------------------------------------------
    //  [VI] HÀM KHỞI TẠO
    //  [VI] Đặt tên và mô tả dễ đọc hiển thị trong danh sách chiến lược của
    //       Quantower. Gọi một lần khi chiến lược được nạp.
    // =========================================================================
    public OneshotFVGStrategy() : base()
    {
        this.Name = "Oneshot 2026 X-Pro";
        this.Description = "ICT FVG + EMA | Oneshot 2026 X-Pro (Telegram alerts + reconnect-recovery)";
    }

    // =========================================================================
    //  OnRun
    //  Entry point called by Quantower when the user clicks "Run".
    //  Validates required inputs, loads historical data, attaches the EMA
    //  indicator, subscribes to position/order events, and starts the timers.
    // -------------------------------------------------------------------------
    //  [VI] OnRun
    //  [VI] Điểm vào, được Quantower gọi khi người dùng bấm "Run".
    //  [VI] Kiểm tra các đầu vào bắt buộc, nạp dữ liệu lịch sử, gắn chỉ báo EMA,
    //       đăng ký sự kiện vị thế/lệnh và khởi động các timer.
    // =========================================================================
    protected override void OnRun()
    {
        // Abort immediately if no symbol or account is configured.
        // [VI] Dừng ngay nếu chưa cấu hình symbol hoặc tài khoản.
        if (CurrentSymbol == null)
        {
            Log("[EN] No Symbol selected.", StrategyLoggingLevel.Error);
            Log("[VI] Chưa chọn Symbol.", StrategyLoggingLevel.Error);
            Stop(); return;
        }
        if (CurrentAccount == null)
        {
            Log("[EN] No Account selected.", StrategyLoggingLevel.Error);
            Log("[VI] Chưa chọn tài khoản.", StrategyLoggingLevel.Error);
            Stop(); return;
        }

        // The Symbol and the Account must live on the SAME broker connection.
        // Otherwise the strategy would feed off one connection while trying to
        // trade on another — exactly the "symbol does not belong to the broker"
        // failure that also surfaces after a bad reconnect.
        // [VI] Symbol và Account phải thuộc CÙNG một kết nối broker. Nếu không, bot
        // [VI] sẽ lấy dữ liệu từ kết nối này nhưng đặt lệnh ở kết nối khác — đúng
        // [VI] lỗi "symbol không thuộc broker" cũng hay xảy ra sau khi reconnect lỗi.
        if (CurrentSymbol.ConnectionId != CurrentAccount.ConnectionId)
        {
            Log("[EN] Symbol and Account are on different connections — aborting.", StrategyLoggingLevel.Error);
            Log("[VI] Symbol và Account thuộc hai kết nối khác nhau — dừng lại.", StrategyLoggingLevel.Error);
            Stop(); return;
        }

        // Capture the instrument identity (not just the object reference) so the
        // watchdog can re-resolve a fresh, live symbol after a reconnect.
        // [VI] Lưu danh tính instrument (không chỉ con trỏ object) để watchdog có
        // [VI] thể tìm lại symbol live mới sau khi reconnect.
        _symbolId     = CurrentSymbol.Id;
        _symbolName   = CurrentSymbol.Name;
        _connectionId = CurrentSymbol.ConnectionId;
        _accountId    = CurrentAccount.Id;

        // Snapshot the TP multiplier so mid-run UI edits cannot affect open trades.
        // [VI] Chụp lại hệ số TP để việc sửa UI giữa chừng không ảnh hưởng lệnh đang mở.
        tpMultiplierInternal = this.TpMultiplier;

        // Request enough history that EMA(EmaPeriod) is fully "warmed up" at the trading
        // edge (≈6× the EMA length, min 500), so the bot's EMA matches the chart's EMA.
        // A short 500-bar load leaves a long EMA under-converged, which can flip the
        // c0 > EMA trend filter near the line and disagree with the indicator.
        // [VI] Yêu cầu đủ lịch sử để EMA(EmaPeriod) "ấm" hẳn ở mép giao dịch (≈6× độ dài EMA,
        // [VI] tối thiểu 500), để EMA của bot khớp EMA trên chart. Chỉ nạp 500 nến khiến EMA
        // [VI] dài chưa hội tụ → bộ lọc c0 > EMA có thể lệch với indicator khi giá sát đường.
        _hdm = CurrentSymbol.GetHistory(CurrentPeriod, HistoryType.Last, Math.Max(500, EmaPeriod * 6));

        // Build the EMA indicator and attach it to the historical data feed so
        // it is recalculated automatically whenever new bars arrive.
        // [VI] Tạo chỉ báo EMA và gắn vào nguồn dữ liệu lịch sử để nó tự động
        // [VI] tính lại mỗi khi có nến mới.
        _ema = Core.Indicators.BuiltIn.EMA(EmaPeriod, PriceType.Close);
        _hdm.AddIndicator(_ema);

        // Subscribe to platform-wide events for positions and orders.
        // [VI] Đăng ký các sự kiện toàn nền tảng cho vị thế và lệnh.
        Core.Instance.PositionAdded += OnPositionAdded;
        Core.Instance.PositionRemoved += OnPositionRemoved;
        Core.Instance.OrderRemoved += OnOrderRemoved;
        // Track every fill so we can report accurate realized P/L per trade.
        // [VI] Theo dõi mọi lần khớp để báo cáo Lãi/Lỗ thực hiện chính xác mỗi lệnh.
        Core.Instance.TradeAdded += OnTradeAdded;

        // Start the main polling timer (fires after a 2-second delay, then every 1 second).
        // [VI] Khởi động timer polling chính (chạy sau 2 giây, rồi mỗi 1 giây).
        _mainPoller = new Timer(_ => OnPoll(), null, 2000, PollIntervalMs);
        // Start the watchdog timer (fires every 30 seconds).
        // [VI] Khởi động timer watchdog (chạy mỗi 30 giây).
        _watchdogTimer = new Timer(_ => OnWatchdog(), null, WatchdogIntervalMs, WatchdogIntervalMs);

        Log(string.Format("[EN] Strategy started — polling every {0}ms | EMA:{1} | Lookback:{2} | UTC Offset:{3:+#;-#;0}h",
            PollIntervalMs, EmaPeriod, BarLookback, SessionUTCOffset), StrategyLoggingLevel.Info);
        Log(string.Format("[VI] Đã khởi động chiến lược — hỏi vòng mỗi {0}ms | EMA:{1} | Nhìn lại:{2} nến | Lệch UTC:{3:+#;-#;0}h",
            PollIntervalMs, EmaPeriod, BarLookback, SessionUTCOffset), StrategyLoggingLevel.Info);

        // Build marker — lets us confirm in the log that the reconnect-recovery
        // build (V2.2) is actually loaded (Quantower caches the old assembly
        // across a simple stop/start of the strategy instance).
        // [VI] Dấu mốc build — để xác nhận trong log rằng bản fix reconnect (V2.2)
        // [VI] đã được nạp (Quantower giữ assembly cũ khi chỉ stop/start instance).
        Log(string.Format("[BUILD] V2.3 Tele (Telegram alerts={0}) | reconnect-recovery active | symbol={1} id={2} conn={3} | feedStaleSec={4}",
            UseTelegramAlerts ? "ON" : "OFF", _symbolName, _symbolId, _connectionId, FeedStaleSec), StrategyLoggingLevel.Info);

        PrintBanner("RUNNING");

        // Telegram start-up notification.
        // [VI] Thông báo Telegram khi khởi động.
        SendTelegram("🟢 The Oneshot Trading System is now active.");
    }

    // =========================================================================
    //  POLL
    //  The main heartbeat of the strategy. Fires every PollIntervalMs.
    //
    //  Phase 1 – Warmup: waits until enough bars are loaded, then scans all
    //            historical bars to build the initial FVG zone lists.
    //  Phase 2 – Live: detects when a new bar has closed and processes it.
    // -------------------------------------------------------------------------
    //  [VI] POLL (VÒNG LẶP CHÍNH)
    //  [VI] Nhịp tim chính của chiến lược. Chạy mỗi PollIntervalMs.
    //  [VI] Pha 1 – Warmup: chờ đủ nến, sau đó quét toàn bộ nến lịch sử để
    //       dựng danh sách vùng FVG ban đầu.
    //  [VI] Pha 2 – Live: phát hiện khi có nến mới đóng và xử lý nó.
    // =========================================================================
    private void OnPoll()
    {
        if (_hdm == null) return;
        int count = _hdm.Count;
        // We need at least EmaPeriod + 5 bars before the EMA is reliable.
        // [VI] Cần ít nhất EmaPeriod + 5 nến để EMA đáng tin cậy.
        int needed = EmaPeriod + 5;

        // --- Phase 1: Warmup ---
        // [VI] --- Pha 1: Warmup ---
        if (!_warmedUp)
        {
            // Not enough bars yet — log progress every 10 seconds and wait.
            // [VI] Chưa đủ nến — ghi tiến độ mỗi 10 giây và chờ.
            if (count < needed)
            {
                int elapsed = (int)(DateTime.UtcNow.Second);
                if (elapsed % 10 == 0)
                {
                    Log(string.Format("[EN]   [POLL] Loading history... {0}/{1} bars (waiting for broker to sync prior-session bars)", count, needed), StrategyLoggingLevel.Info);
                    Log(string.Format("[VI]   [POLL] Đang nạp lịch sử... {0}/{1} nến (chờ sàn đồng bộ nến phiên cũ)", count, needed), StrategyLoggingLevel.Info);
                }
                return;
            }

            // Prevent running the warmup scan more than once.
            // [VI] Ngăn quét warmup chạy quá một lần.
            if (_warmupAttempted) return;
            _warmupAttempted = true;

            Log(string.Format("[EN]   [POLL] {0} bars ready — running warmup scan...", count), StrategyLoggingLevel.Info);
            Log(string.Format("[VI]   [POLL] Đã đủ {0} nến — đang chạy quét warmup...", count), StrategyLoggingLevel.Info);

            // Clear any stale zone data before the full historical scan.
            // [VI] Xóa dữ liệu vùng cũ trước khi quét toàn bộ lịch sử.
            _bullTop.Clear(); _bullBtm.Clear(); _bullBar.Clear();
            _bearTop.Clear(); _bearBtm.Clear(); _bearBar.Clear();
            _barCount = 0;

            // Scan bars from oldest to newest (index count-1 down to 2).
            // executeTrades=false means FVG zones are built but no orders are placed.
            // [VI] Quét nến từ cũ đến mới (chỉ số count-1 xuống 2).
            // [VI] executeTrades=false nghĩa là chỉ dựng vùng FVG, không đặt lệnh.
            for (int i = count - 1; i >= 2; i--)
                ProcessBar(i, executeTrades: false);

            // If the EMA is still NaN the indicator hasn't calculated yet — retry next poll.
            // [VI] Nếu EMA vẫn NaN tức chỉ báo chưa tính xong — thử lại ở lần poll sau.
            if (double.IsNaN(_lastEmaVal))
            {
                Log("[EN] [POLL] EMA still NaN after warmup — retrying next poll.", StrategyLoggingLevel.Error);
                Log("[VI] [POLL] EMA vẫn NaN sau warmup — sẽ thử lại ở lần hỏi vòng kế tiếp.", StrategyLoggingLevel.Error);
                _warmupAttempted = false;
                return;
            }

            _warmedUp = true;
            Log(string.Format("[EN] ✅ [POLL] Warmup successful — EMA({0}): {1:F2} | Bull FVG:{2} | Bear FVG:{3}",
                EmaPeriod, _lastEmaVal, _bullTop.Count, _bearTop.Count), StrategyLoggingLevel.Info);
            Log(string.Format("[VI] ✅ [POLL] Warmup thành công — EMA({0}): {1:F2} | FVG tăng:{2} | FVG giảm:{3}",
                EmaPeriod, _lastEmaVal, _bullTop.Count, _bearTop.Count), StrategyLoggingLevel.Info);

            // Seed _lastProcessedBar so the very next poll doesn't re-process bar [1].
            // [VI] Gán mồi _lastProcessedBar để lần poll kế tiếp không xử lý lại nến [1].
            var seed = _hdm[1] as HistoryItemBar;
            if (seed != null) _lastProcessedBar = seed.TimeLeft;
            return;
        }

        // --- Phase 2: Live bar detection — process EVERY newly-closed bar, never skip ---
        // PREVIOUS BUG: only _hdm[1] (the single latest closed bar) was processed each
        // poll. Whenever the feed advanced by more than one bar between polls — startup
        // catch-up, a data burst, a reconnect, or replay/fast mode — the intermediate
        // bars were skipped entirely: their FVG zones were never built and their signals
        // never evaluated. That left the zone lists permanently incomplete (e.g. Bear:0),
        // so later retraces found no zone and produced "No Signal", and real entries were
        // missed. We now replay EVERY unprocessed closed bar in chronological order so no
        // FVG zone is ever lost. Only the most recent bar may place an order; older
        // catch-up bars rebuild zones only (executeTrades=false) to avoid entering late
        // on a bar that already closed minutes ago.
        // [VI] --- Pha 2: Phát hiện nến — xử lý MỌI nến vừa đóng, KHÔNG bỏ sót ---
        // [VI] LỖI TRƯỚC ĐÂY: mỗi vòng poll chỉ xử lý _hdm[1] (đúng 1 nến đóng gần nhất).
        // [VI] Khi nguồn nhảy hơn 1 nến giữa 2 lần poll (bù lúc khởi động, nạp dồn, kết nối
        // [VI] lại, hay chế độ replay) thì các nến ở giữa bị BỎ HẲN: không dựng vùng FVG,
        // [VI] không xét tín hiệu. Khiến danh sách vùng thiếu vĩnh viễn (vd Bear:0), nên về
        // [VI] sau giá retrace lại không thấy vùng → "No Signal", và bỏ lỡ lệnh thật. Giờ ta
        // [VI] replay MỌI nến chưa xử lý theo đúng thứ tự thời gian để không mất vùng FVG nào.
        // [VI] Chỉ nến mới nhất được đặt lệnh; nến bù chỉ dựng vùng (executeTrades=false) để
        // [VI] không vào lệnh trễ trên nến đã đóng từ mấy phút trước.
        if (count < 3) return;

        // Count how many of the most recent closed bars are newer than the last one we
        // already processed (walk from _hdm[1] outward until we hit a known/old bar).
        // [VI] Đếm xem có bao nhiêu nến đóng gần nhất mới hơn nến đã xử lý cuối cùng
        // [VI] (đi từ _hdm[1] ra xa cho tới khi gặp nến đã biết/cũ).
        int newBars = 0;
        for (int idx = 1; idx + 2 < count; idx++)
        {
            var b = _hdm[idx] as HistoryItemBar;
            if (b == null) break;
            if (_lastProcessedBar != DateTime.MinValue && b.TimeLeft <= _lastProcessedBar) break;
            newBars++;
        }
        if (newBars == 0) return;

        _lastBarTime = DateTime.UtcNow;
        _lastHdmBarTime = DateTime.UtcNow;

        // Replay oldest-first (highest index → 1) so FVG zones build in time order.
        // Trading rule: LIVE (ReplayMode OFF) → only the newest bar may enter, so a burst
        // never fires a late entry on a stale bar. REPLAY (ReplayMode ON) → EVERY bar may
        // enter, so signals on replayed/catch-up bars are not skipped and the bot matches
        // the indicator, which marks every signal.
        // [VI] Replay từ cũ đến mới (chỉ số cao → 1) để dựng vùng FVG đúng thứ tự thời gian.
        // [VI] Luật vào lệnh: THẬT (Replay TẮT) → chỉ nến mới nhất được vào, để nạp dồn không
        // [VI] bắn lệnh trễ trên nến cũ. REPLAY (Replay BẬT) → MỌI nến đều được vào, để tín
        // [VI] hiệu trên nến replay/bù không bị bỏ và bot khớp indicator (đánh dấu mọi tín hiệu).
        for (int k = newBars; k >= 1; k--)
        {
            var bar = _hdm[k] as HistoryItemBar;
            if (bar == null) continue;
            _lastProcessedBar = bar.TimeLeft;
            bool isNewest = (k == 1);
            // Enter on the newest bar (live) or on every bar (replay/backtest).
            // [VI] Vào lệnh ở nến mới nhất (thật) hoặc mọi nến (replay/backtest).
            bool tradeThisBar = ReplayMode || isNewest;

            // Stay quiet here when outside the time filter — ProcessBar prints the single
            // "Outside of Trading Timefilter" line instead of these per-bar messages.
            // [VI] Im lặng ở đây khi ngoài bộ lọc giờ — ProcessBar sẽ in dòng "Outside..."
            // [VI] thay cho các message theo nến này.
            bool barInHours = IsWithinTradingHours(bar.TimeLeft);
            if (isNewest)
            {
                if (barInHours)
                {
                    Log(string.Format("[EN]   [POLL] New bar detected @ {0:HH:mm}", bar.TimeLeft), StrategyLoggingLevel.Info);
                    Log(string.Format("[VI]   [POLL] Phát hiện nến mới lúc {0:HH:mm}", bar.TimeLeft), StrategyLoggingLevel.Info);
                }
            }
            else if (!ReplayMode && barInHours)
            {
                // Live catch-up bar: zones only, no entry (its own bar summary is suppressed).
                // [VI] Nến bù khi chạy thật: chỉ dựng vùng, không vào lệnh (không in tóm tắt nến).
                Log(string.Format("[EN]   [POLL] Catch-up bar @ {0:HH:mm} — building FVG zones only (no entry).", bar.TimeLeft), StrategyLoggingLevel.Info);
                Log(string.Format("[VI]   [POLL] Nến bù @ {0:HH:mm} — chỉ dựng vùng FVG (không vào lệnh).", bar.TimeLeft), StrategyLoggingLevel.Info);
            }
            // In replay mode, catch-up bars trade and log their own "Bar # → signal" summary.
            // [VI] Ở chế độ replay, nến bù được vào lệnh và tự in dòng tóm tắt "Nến # → tín hiệu".

            ProcessBar(k, executeTrades: tradeThisBar);
        }
    }

    // =========================================================================
    //  PROCESS BAR
    //  The core signal-detection routine. Reads three consecutive completed
    //  bars (b0 = current, b1 = previous, b2 = two bars ago), calculates the
    //  EMA, detects new FVG zones, checks for valid zone touches, tests for
    //  reversal candle confirmation, and if all conditions align fires a trade.
    //
    //  hdmIndex : the _hdm[] index of the bar to treat as "current" (b0).
    //  executeTrades : false during warmup (zone building only), true for live.
    // -------------------------------------------------------------------------
    //  [VI] PROCESS BAR (XỬ LÝ NẾN)
    //  [VI] Hàm cốt lõi phát hiện tín hiệu. Đọc 3 nến đã đóng liên tiếp
    //       (b0 = hiện tại, b1 = trước đó, b2 = hai nến trước), tính EMA,
    //       phát hiện vùng FVG mới, kiểm tra chạm vùng hợp lệ, kiểm tra nến
    //       đảo chiều xác nhận, và nếu mọi điều kiện khớp thì vào lệnh.
    //  [VI] hdmIndex : chỉ số _hdm[] của nến coi là "hiện tại" (b0).
    //  [VI] executeTrades : false khi warmup (chỉ dựng vùng), true khi chạy thật.
    // =========================================================================
    private void ProcessBar(int hdmIndex, bool executeTrades)
    {
        // Safety guard: ensure three bars are available.
        // [VI] Canh an toàn: bảo đảm có sẵn 3 nến.
        if (_hdm == null || _hdm.Count < hdmIndex + 3) return;
        var b0 = _hdm[hdmIndex] as HistoryItemBar;       // Most recent completed bar  // [VI] Nến đã đóng gần nhất
        var b1 = _hdm[hdmIndex + 1] as HistoryItemBar;   // One bar before b0          // [VI] Một nến trước b0
        var b2 = _hdm[hdmIndex + 2] as HistoryItemBar;   // Two bars before b0         // [VI] Hai nến trước b0
        if (b0 == null || b1 == null || b2 == null) return;
        _barCount++;

        // Extract OHLC values from each bar for readability.
        // [VI] Tách các giá OHLC từ mỗi nến cho dễ đọc.
        double c0 = b0.Close, o0 = b0.Open, h0 = b0.High, l0 = b0.Low;
        double c1 = b1.Close, o1 = b1.Open, h1 = b1.High, l1 = b1.Low;
        double h2 = b2.High, l2 = b2.Low;

        // Read the EMA value for this bar index. Skip if not yet calculated.
        // [VI] Đọc giá trị EMA cho chỉ số nến này. Bỏ qua nếu chưa tính.
        double ema = _ema.GetValue(hdmIndex);
        if (double.IsNaN(ema))
        {
            // [DIAG] EMA not ready yet → the whole bar is skipped silently (no FVG, no
            // signal, no bar log). Surface it live so a missing entry can be traced to
            // an unavailable EMA value rather than guessed.
            // [VI] [CHẨN ĐOÁN] EMA chưa sẵn sàng → cả nến bị bỏ qua âm thầm (không dựng
            // [VI] FVG, không tín hiệu, không log nến). Ghi log khi chạy thật để truy được
            // [VI] việc thiếu lệnh là do EMA chưa có giá trị, thay vì phải đoán.
            if (executeTrades)
            {
                Log("[EN] ⚠ [SKIP BAR] EMA not ready (NaN) — whole bar skipped, no signal evaluated.", StrategyLoggingLevel.Error);
                Log("[VI] ⚠ [BỎ NẾN] EMA chưa sẵn sàng (NaN) — bỏ cả nến, không xét tín hiệu.", StrategyLoggingLevel.Error);
            }
            return;
        }
        _lastEmaVal = ema;
        // Trend is bullish if price closed above the EMA, bearish if below.
        // [VI] Xu hướng tăng nếu giá đóng trên EMA, giảm nếu dưới.
        _isUptrend = c0 > ema;

        string trend = _isUptrend ? "UPTREND ▲" : "DOWNTREND ▼";

        // --- FVG Zone Detection ---
        // [VI] --- Phát hiện vùng FVG ---
        // Bullish FVG: a gap exists when b0's low is above b2's high
        // (the middle candle b1 "jumped" upward, leaving unfilled space).
        // [VI] FVG tăng: có khoảng trống khi đáy b0 cao hơn đỉnh b2
        // [VI] (nến giữa b1 "nhảy" lên, để lại khoảng chưa lấp).
        if (l0 > h2) { _bullTop.Add(l0); _bullBtm.Add(h2); _bullBar.Add(_barCount); }
        // Bearish FVG: a gap exists when b0's high is below b2's low
        // (the middle candle b1 "dropped" downward, leaving unfilled space).
        // [VI] FVG giảm: có khoảng trống khi đỉnh b0 thấp hơn đáy b2
        // [VI] (nến giữa b1 "rớt" xuống, để lại khoảng chưa lấp).
        if (h0 < l2) { _bearTop.Add(l2); _bearBtm.Add(h0); _bearBar.Add(_barCount); }
        // Remove zones that have exceeded the BarLookback expiry window.
        // [VI] Xóa các vùng đã vượt cửa sổ hết hạn BarLookback.
        PruneExpiredZones();

        // --- Trading-hours log gate ---
        // When the session time filter is ON and this bar is OUTSIDE the chosen window,
        // suppress every detailed per-bar message (bar summary / retrace / signal) and emit
        // a single line instead. FVG zones were already built above so they stay correct;
        // no entry is taken (also blocked later by IsWithinTradingHours).
        // [VI] --- Cổng log theo giờ giao dịch ---
        // [VI] Khi bật bộ lọc giờ và nến này NGOÀI khung đã chọn, ẩn mọi message chi tiết của
        // [VI] nến (tóm tắt / retrace / tín hiệu) và chỉ in MỘT dòng. Vùng FVG đã dựng ở trên
        // [VI] nên vẫn đúng; không vào lệnh (cũng đã bị IsWithinTradingHours chặn bên dưới).
        if (executeTrades && !IsWithinTradingHours(b0.TimeLeft))
        {
            _lastSignal = "Outside session";
            Log("[EN] ⏰ Outside of Trading Timefilter - No Message", StrategyLoggingLevel.Info);
            Log("[VI] ⏰ Ngoài khung giờ giao dịch - No Message", StrategyLoggingLevel.Info);
            return;
        }

        // --- Zone Touch Detection ---
        // [VI] --- Phát hiện chạm vùng ---
        // Check whether b0 penetrated any active bullish FVG zone
        // (only within the BarLookback window and after the zone was formed).
        // [VI] Kiểm tra b0 có xuyên vào vùng FVG tăng nào đang hoạt động không
        // [VI] (chỉ trong cửa sổ BarLookback và sau khi vùng được tạo).
        // Also capture WHICH zone was touched (its creation bar + bounds) so we can
        // print an early retrace heads-up and dedup it per zone.
        // [VI] Đồng thời lưu lại VÙNG nào bị chạm (nến tạo + biên) để in cảnh báo
        // [VI] retrace sớm và chống lặp theo từng vùng.
        // Touch is valid if EITHER the current bar b0 OR the previous bar b1 penetrated
        // the zone — the pullback candle (b1) often taps the gap while b0 is the reversal.
        // [VI] Chạm hợp lệ nếu nến hiện tại b0 HOẶC nến trước b1 xuyên vào vùng — nến hồi
        // [VI] (b1) thường chạm khoảng trống còn b0 là nến đảo chiều.
        bool touchBull = false;
        int touchBullZone = -1; double touchBullTop = double.NaN, touchBullBtm = double.NaN;
        for (int i = 0; i < _bullTop.Count && !touchBull; i++)
            if (_barCount > _bullBar[i] && _barCount <= _bullBar[i] + BarLookback)
                if ((l0 <= _bullTop[i] && h0 >= _bullBtm[i]) ||
                    (l1 <= _bullTop[i] && h1 >= _bullBtm[i]))
                { touchBull = true; touchBullZone = _bullBar[i]; touchBullTop = _bullTop[i]; touchBullBtm = _bullBtm[i]; }

        // Check whether b0 penetrated any active bearish FVG zone.
        // [VI] Kiểm tra b0 có xuyên vào vùng FVG giảm nào đang hoạt động không.
        bool touchBear = false;
        int touchBearZone = -1; double touchBearTop = double.NaN, touchBearBtm = double.NaN;
        for (int i = 0; i < _bearTop.Count && !touchBear; i++)
            if (_barCount > _bearBar[i] && _barCount <= _bearBar[i] + BarLookback)
                if ((h0 >= _bearBtm[i] && l0 <= _bearTop[i]) ||
                    (h1 >= _bearBtm[i] && l1 <= _bearTop[i]))
                { touchBear = true; touchBearZone = _bearBar[i]; touchBearTop = _bearTop[i]; touchBearBtm = _bearBtm[i]; }

        // --- Retrace-into-FVG observation log (early "watch" heads-up) ---
        // When price taps an active FVG in the trend direction, log a heads-up so you
        // can watch for the reversal candle that would confirm an entry. Logged only
        // live, while flat, and once per zone (anti-spam). This is observation only —
        // it does NOT place any order; the actual entry still needs the reversal candle.
        // [VI] --- Log quan sát: giá retrace về vùng FVG (cảnh báo sớm) ---
        // [VI] Khi giá chạm một vùng FVG đang hoạt động theo đúng hướng xu hướng, ghi
        // [VI] log để bạn canh nến đảo chiều (điều kiện vào lệnh). Chỉ ghi khi chạy
        // [VI] thật, đang rảnh (chưa có lệnh) và mỗi vùng một lần (chống spam). Đây chỉ
        // [VI] là quan sát — KHÔNG đặt lệnh; muốn vào lệnh vẫn cần nến đảo chiều.
        if (executeTrades && !_inTrade)
        {
            // Uptrend + tapped a Bull FVG → get ready to watch for a Long.
            // [VI] Xu hướng tăng + chạm vùng Bull FVG → chuẩn bị canh Long.
            if (touchBull && _isUptrend && touchBullZone != _lastRetraceBullZone)
            {
                _lastRetraceBullZone = touchBullZone;
                Log(string.Format("[EN] 👀 [RETRACE → WATCH LONG] Uptrend — price retraced into a Bull FVG [{0:F2} – {1:F2}] @ {2:F2}. Waiting for a bullish reversal candle to confirm a Long.",
                    touchBullBtm, touchBullTop, c0), StrategyLoggingLevel.Info);
                Log(string.Format("[VI] 👀 [RETRACE → CANH LONG] Xu hướng tăng — giá retrace về vùng Bull FVG [{0:F2} – {1:F2}] @ {2:F2}. Đang chờ nến đảo chiều tăng để xác nhận Long.",
                    touchBullBtm, touchBullTop, c0), StrategyLoggingLevel.Info);
            }
            // Downtrend + tapped a Bear FVG → get ready to watch for a Short.
            // [VI] Xu hướng giảm + chạm vùng Bear FVG → chuẩn bị canh Short.
            if (touchBear && !_isUptrend && touchBearZone != _lastRetraceBearZone)
            {
                _lastRetraceBearZone = touchBearZone;
                Log(string.Format("[EN] 👀 [RETRACE → WATCH SHORT] Downtrend — price retraced into a Bear FVG [{0:F2} – {1:F2}] @ {2:F2}. Waiting for a bearish reversal candle to confirm a Short.",
                    touchBearBtm, touchBearTop, c0), StrategyLoggingLevel.Info);
                Log(string.Format("[VI] 👀 [RETRACE → CANH SHORT] Xu hướng giảm — giá retrace về vùng Bear FVG [{0:F2} – {1:F2}] @ {2:F2}. Đang chờ nến đảo chiều giảm để xác nhận Short.",
                    touchBearBtm, touchBearTop, c0), StrategyLoggingLevel.Info);
            }
        }

        // --- Reversal Candle Confirmation (ICT displacement pattern) ---
        // [VI] --- Xác nhận nến đảo chiều (mẫu hình displacement ICT) ---
        // The first two parts are identical in both modes: b1 direction + b0 direction.
        // Only the close-break reference changes with UseBodyReversal:
        //   OFF (Wick Break, original): bull c0 > h1 ; bear c0 < l1.
        //   ON  (Body Break):           bull c0 > o1 ; bear c0 < o1.
        // [VI] Hai phần đầu giống nhau ở cả hai chế độ: hướng b1 + hướng b0.
        // [VI] Chỉ mốc so sánh giá đóng đổi theo UseBodyReversal:
        // [VI]   OFF (Wick Break, gốc): tăng c0 > h1 ; giảm c0 < l1.
        // [VI]   ON  (Body Break):       tăng c0 > o1 ; giảm c0 < o1.
        bool bullReversal = c1 < o1 && c0 > o0 && (UseBodyReversal ? c0 > o1 : c0 > h1);
        bool bearReversal = c1 > o1 && c0 < o0 && (UseBodyReversal ? c0 < o1 : c0 < l1);

        // --- Final Signal Logic ---
        // [VI] --- Logic tín hiệu cuối cùng ---
        // A Long signal requires: bullish FVG touch + bullish reversal candle + price above EMA.
        // [VI] Tín hiệu Long cần: chạm FVG tăng + nến đảo chiều tăng + giá trên EMA.
        bool goLong = touchBull && bullReversal && c0 > ema;
        // A Short signal requires: bearish FVG touch + bearish reversal candle + price below EMA.
        // [VI] Tín hiệu Short cần: chạm FVG giảm + nến đảo chiều giảm + giá dưới EMA.
        bool goShort = touchBear && bearReversal && c0 < ema;

        // --- Raw SL Price from Signal Bar ---
        // For Long: SL = lowest point of b0 or b1 (most conservative swing low).
        // For Short: SL = highest point of b0 or b1.
        // [VI] --- Giá SL thô từ nến tín hiệu ---
        // [VI] Long: SL = điểm thấp nhất của b0 hoặc b1 (đáy swing thận trọng nhất).
        // [VI] Short: SL = điểm cao nhất của b0 hoặc b1.
        double rawSL = double.NaN;
        if (goLong) rawSL = Math.Min(l0, l1);
        if (goShort) rawSL = Math.Max(h0, h1);

        // Calculate the point distance from current close to the raw SL.
        // [VI] Tính khoảng cách (điểm) từ giá đóng hiện tại đến SL thô.
        double slDist = double.NaN;
        if (goLong && !double.IsNaN(rawSL)) slDist = c0 - rawSL;
        if (goShort && !double.IsNaN(rawSL)) slDist = rawSL - c0;

        // --- SL Width Validation ---
        // [VI] --- Kiểm tra độ rộng SL ---
        // slOK = true if SL is within the user's MaxSLPoints threshold.
        // [VI] slOK = true nếu SL nằm trong ngưỡng MaxSLPoints của người dùng.
        bool slOK = double.IsNaN(slDist) || slDist <= MaxSLPoints;
        // useCapped = true if we are allowed to replace an oversized SL with a fixed cap.
        // [VI] useCapped = true nếu được phép thay SL quá rộng bằng mức trần cố định.
        bool useCapped = !slOK && UseCappedSL;
        // tradeOK = true if the trade should proceed (either SL is fine, or we are capping it).
        // [VI] tradeOK = true nếu lệnh được tiếp tục (SL ổn, hoặc đang dùng trần SL).
        bool tradeOK = slOK || useCapped;

        // Build a human-readable label for the log and metrics display.
        // [VI] Tạo nhãn dễ đọc cho log và bảng chỉ số.
        string capTag = useCapped ? " [CAPPED SL]" : "";
        string wideTag = !slOK && !UseCappedSL ? " [SL TOO WIDE]" : "";
        string sigTxt = (goLong && tradeOK) ? "▲ LONG" + capTag
                       : (goShort && tradeOK) ? "▼ SHORT" + capTag
                       : (goLong || goShort) ? (goLong ? "▲" : "▼") + wideTag
                       : "- No Signal";
        _lastSignal = sigTxt;

        // Log bar summary to the strategy output panel during live operation.
        // [VI] Ghi tóm tắt nến ra bảng output của chiến lược khi chạy thật.
        if (executeTrades)
        {
            Log(string.Format("[EN] ━━ Bar #{0}  {1}  EMA:{2:F2}  Close:{3:F2}  Bull:{4} Bear:{5}  → {6}",
                _barCount, trend, ema, c0, _bullTop.Count, _bearTop.Count, sigTxt), StrategyLoggingLevel.Info);
            Log(string.Format("[VI] ━━ Nến #{0}  {1}  EMA:{2:F2}  Đóng:{3:F2}  Tăng:{4} Giảm:{5}  → {6}",
                _barCount, trend, ema, c0, _bullTop.Count, _bearTop.Count, sigTxt), StrategyLoggingLevel.Info);
        }

        // Stop here during warmup (zone building only).
        // [VI] Dừng tại đây khi warmup (chỉ dựng vùng).
        if (!executeTrades) return;

        // --- Diagnostic: a directional signal was detected — log EXACTLY why it does
        //     NOT become an entry (or stay silent and fall through to the entry below).
        //     Pure logging, changes no trading behaviour. Use it to tell a real bug
        //     (stuck flag / not actually flat) apart from an expected skip (in-trade /
        //     session / rate limit). If a signal shows on the indicator but you DON'T
        //     even see a "▲ LONG / ▼ SHORT" bar line above, then the bot didn't detect
        //     it at all (zone-list / EMA / lookback difference), not a guard block.
        // [VI] --- Chẩn đoán: đã phát hiện tín hiệu có hướng — ghi RÕ vì sao KHÔNG vào
        // [VI]     lệnh (hoặc im lặng rơi xuống nhánh vào lệnh bên dưới). Chỉ log, KHÔNG
        // [VI]     đổi hành vi giao dịch. Dùng để phân biệt LỖI thật (kẹt cờ / chưa flat)
        // [VI]     với bỏ qua hợp lệ (đang có lệnh / ngoài giờ / giới hạn). Nếu indicator
        // [VI]     có tín hiệu mà KHÔNG thấy dòng "▲ LONG / ▼ SHORT" ở trên → bot không
        // [VI]     hề phát hiện (khác zone/EMA/lookback), chứ không phải bị cổng chặn.
        if (goLong || goShort)
        {
            string why = null;
            if (!tradeOK) why = "SL too wide & capped SL OFF / SL quá rộng và TẮT trần SL";
            else if (double.IsNaN(rawSL)) why = "no raw SL price / thiếu giá SL gốc";
            else if (!IsWithinTradingHours(b0.TimeLeft)) why = "outside trading hours / ngoài giờ giao dịch";
            else if (_inTrade) why = "already in a trade — no pyramiding / đang có lệnh — không nhồi";
            else if (_pendingFill) why = "waiting for previous fill confirm / đang chờ xác nhận khớp lệnh trước";
            else if (_pendingReverse) why = "reverse in progress / đang xử lý đảo chiều";
            else if (_flattening) why = "flatten in progress / đang đóng vị thế";
            else if (Core.Instance.Positions.Any(p => IsOurs(p.Symbol) && IsOurs(p.Account)))
                why = "an open position still exists on this symbol+account / vẫn còn vị thế mở trên symbol+account";
            else if (!IsWithinRateLimit("DIAG")) why = "rate limit (Max Trades Per Minute) reached / chạm giới hạn lệnh mỗi phút";

            if (why != null)
            {
                Log("[EN] ⛔ [NO ENTRY] " + (goLong ? "LONG" : "SHORT") + " signal detected but NOT entered — reason: " + why, StrategyLoggingLevel.Info);
                Log("[VI] ⛔ [KHÔNG VÀO LỆNH] Có tín hiệu " + (goLong ? "LONG" : "SHORT") + " nhưng KHÔNG vào — lý do: " + why, StrategyLoggingLevel.Info);
            }
        }

        // Also stop if no valid signal or SL data.
        // [VI] Cũng dừng nếu không có tín hiệu hợp lệ hoặc thiếu dữ liệu SL.
        if (!tradeOK || double.IsNaN(rawSL)) return;

        // Skip if outside the configured trading session window.
        // [VI] Bỏ qua nếu ngoài khung phiên giao dịch đã cấu hình.
        if (!IsWithinTradingHours(b0.TimeLeft)) return;

        // --- Reverse Trade Logic ---
        // If we are already in a trade and a new opposite signal appears, flatten first
        // and then re-enter in the new direction.
        // [VI] --- Logic đảo lệnh ---
        // [VI] Nếu đang có lệnh và xuất hiện tín hiệu ngược mới, đóng vị thế trước
        // [VI] rồi vào lại theo hướng mới.
        if (_inTrade && !_pendingFill && !_pendingReverse && AllowReverse)
        {
            bool reverseToLong = goLong && !_isLong;
            bool reverseToShort = goShort && _isLong;

            if (reverseToLong || reverseToShort)
            {
                if (!IsWithinRateLimit("REVERSE")) return;
                double newSLDist = reverseToLong ? c0 - rawSL : rawSL - c0;
                double newTP = reverseToLong ? c0 + newSLDist * tpMultiplierInternal : c0 - newSLDist * tpMultiplierInternal;
                int newContracts = GetContractCount(newSLDist);

                // Store the reverse trade parameters so OnPositionRemoved can fire the new entry.
                // [VI] Lưu tham số lệnh đảo để OnPositionRemoved kích hoạt lệnh mới.
                _pendingReverse = true;
                _reverseIsLong = reverseToLong;
                _reverseSLPrice = rawSL;
                _reverseTPPrice = newTP;
                _reverseSLDist = newSLDist;
                _reverseContracts = newContracts;

                FlattenPosition("Reverse signal");
                return;
            }
        }

        // --- New Entry Guard ---
        // Confirm we are truly flat on the platform before placing a new entry.
        // [VI] --- Chốt chặn trước khi vào lệnh mới ---
        // [VI] Xác nhận thực sự không còn vị thế nào trên sàn trước khi vào lệnh mới.
        bool flat2 = !Core.Instance.Positions.Any(p => IsOurs(p.Symbol) && IsOurs(p.Account));
        if (!flat2 || _inTrade || _pendingFill || _pendingReverse || _flattening) return;

        // --- Fire Entry ---
        // [VI] --- Bắn lệnh vào ---
        if (goLong)
        {
            if (!IsWithinRateLimit("LONG")) return;
            int qty = GetContractCount(c0 - rawSL);
            Log(string.Format("[EN]   >> [SIGNAL LONG] Market entry sent first. Qty: {0} | Saved candle RawSL: {1:F2}", qty, rawSL), StrategyLoggingLevel.Trading);
            Log(string.Format("[VI]   >> [TÍN HIỆU LONG] Đã gửi lệnh Market trước. Qty: {0} | Lưu RawSL nến: {1:F2}", qty, rawSL), StrategyLoggingLevel.Trading);
            PlaceEntry(Side.Buy, rawSL, qty);
        }
        else if (goShort)
        {
            if (!IsWithinRateLimit("SHORT")) return;
            int qty = GetContractCount(rawSL - c0);
            Log(string.Format("[EN]   >> [SIGNAL SHORT] Market entry sent first. Qty: {0} | Saved candle RawSL: {1:F2}", qty, rawSL), StrategyLoggingLevel.Trading);
            Log(string.Format("[VI]   >> [TÍN HIỆU SHORT] Đã gửi lệnh Market trước. Qty: {0} | Lưu RawSL nến: {1:F2}", qty, rawSL), StrategyLoggingLevel.Trading);
            PlaceEntry(Side.Sell, rawSL, qty);
        }
    }

    // =========================================================================
    //  PRUNE ZONES
    //  Removes FVG zones from the lists when they have aged past BarLookback
    //  bars. Works backwards through the list so index removal is safe.
    // -------------------------------------------------------------------------
    //  [VI] PRUNE ZONES (DỌN VÙNG HẾT HẠN)
    //  [VI] Xóa các vùng FVG khỏi danh sách khi đã quá BarLookback nến.
    //  [VI] Duyệt ngược danh sách để việc xóa theo chỉ số được an toàn.
    // =========================================================================
    private void PruneExpiredZones()
    {
        int cutoff = _barCount - BarLookback - 1;
        for (int i = _bullBar.Count - 1; i >= 0; i--)
            if (_bullBar[i] < cutoff) { _bullTop.RemoveAt(i); _bullBtm.RemoveAt(i); _bullBar.RemoveAt(i); }
        for (int i = _bearBar.Count - 1; i >= 0; i--)
            if (_bearBar[i] < cutoff) { _bearTop.RemoveAt(i); _bearBtm.RemoveAt(i); _bearBar.RemoveAt(i); }
    }

    // =========================================================================
    //  RATE LIMIT
    //  Enforces MaxTradesPerMinute by keeping a rolling 60-second timestamp
    //  queue. Returns false (and blocks the trade) if the quota is full.
    // -------------------------------------------------------------------------
    //  [VI] RATE LIMIT (GIỚI HẠN TẦN SUẤT)
    //  [VI] Áp MaxTradesPerMinute bằng hàng đợi mốc thời gian trượt 60 giây.
    //  [VI] Trả về false (chặn lệnh) nếu đã đủ hạn mức.
    // =========================================================================
    private bool IsWithinRateLimit(string direction)
    {
        // Expire timestamps older than 60 seconds.
        // [VI] Loại bỏ các mốc thời gian cũ hơn 60 giây.
        DateTime cutoff = DateTime.UtcNow.AddSeconds(-60);
        while (_tradeTimestamps.Count > 0 && _tradeTimestamps.Peek() < cutoff) _tradeTimestamps.Dequeue();
        if (_tradeTimestamps.Count >= MaxTradesPerMinute) return false;
        return true;
    }

    // =========================================================================
    //  PLACE ENTRY
    //  Sends a Market IOC order to enter the trade, arms the debounce timer,
    //  and records the timestamp for rate-limit tracking.
    //  The SL/TP orders are NOT placed here — they are placed after the fill
    //  is confirmed in OnDebounceFlush() → CalculateAndPlaceSLTP().
    // -------------------------------------------------------------------------
    //  [VI] PLACE ENTRY (ĐẶT LỆNH VÀO)
    //  [VI] Gửi lệnh Market IOC để vào lệnh, kích hoạt timer debounce và ghi
    //       lại mốc thời gian cho theo dõi giới hạn tần suất.
    //  [VI] SL/TP KHÔNG đặt ở đây — chúng được đặt sau khi xác nhận khớp trong
    //       OnDebounceFlush() → CalculateAndPlaceSLTP().
    // =========================================================================
    private void PlaceEntry(Side side, double rawSLPrice, int qty)
    {
        // Set direction and save the candle-derived SL for use after fill.
        // [VI] Đặt hướng và lưu SL lấy từ nến để dùng sau khi khớp.
        _isLong = side == Side.Buy;
        _signalRawSLPrice = rawSLPrice;
        _slTPSet = false;
        _activeSLId = null;
        _activeTPId = null;
        _inTrade = true;

        // Start the debounce countdown that will read the fill price after DebounceMs.
        // [VI] Bắt đầu đếm ngược debounce, sẽ đọc giá khớp sau DebounceMs.
        ArmDebounceTimer();

        // Send the market entry order.
        // [VI] Gửi lệnh thị trường vào lệnh.
        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Symbol = CurrentSymbol,
            Account = CurrentAccount,
            Side = side,
            Quantity = qty,
            OrderTypeId = OrderType.Market,
            TimeInForce = TimeInForce.IOC,
            Comment = "Oneshot " + (_isLong ? "Long" : "Short") + " Entry"
        });

        // If the broker rejects the order, roll back the in-trade state completely.
        // [VI] Nếu sàn từ chối lệnh, hoàn tác hoàn toàn trạng thái đang-vào-lệnh.
        if (result.Status == TradingOperationResultStatus.Failure)
        {
            Log("[EN]   Entry FAILED: " + result.Message, StrategyLoggingLevel.Error);
            Log("[VI]   Lệnh vào THẤT BẠI: " + result.Message, StrategyLoggingLevel.Error);
            _pendingReverse = false;
            _inTrade = false;
            lock (_lock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                _pendingFill = false;
            }
            return;
        }

        // Record this trade time for the rate-limit window.
        // [VI] Ghi lại thời điểm lệnh này cho cửa sổ giới hạn tần suất.
        _tradeTimestamps.Enqueue(DateTime.UtcNow);
    }

    // =========================================================================
    //  DEBOUNCE FLUSH
    //  A two-part mechanism that delays SL/TP placement until after the
    //  broker has confirmed the fill:
    //
    //  ArmDebounceTimer() — called right before PlaceEntry sends the order.
    //    Sets _pendingFill=true and starts a one-shot timer for DebounceMs.
    //
    //  OnDebounceFlush() — fires when the timer expires.
    //    Reads the real fill price from the open positions, calculates VWAP
    //    if partially filled across multiple position records, then hands off
    //    to CalculateAndPlaceSLTP().
    // -------------------------------------------------------------------------
    //  [VI] DEBOUNCE FLUSH (HOÃN ĐẶT SL/TP CHO ĐẾN KHI KHỚP)
    //  [VI] Cơ chế hai phần trì hoãn việc đặt SL/TP cho đến khi sàn xác nhận khớp:
    //  [VI] ArmDebounceTimer() — gọi ngay trước khi PlaceEntry gửi lệnh.
    //       Đặt _pendingFill=true và khởi động timer một-lần cho DebounceMs.
    //  [VI] OnDebounceFlush() — chạy khi timer hết giờ.
    //       Đọc giá khớp thật từ các vị thế đang mở, tính VWAP nếu khớp lẻ
    //       trên nhiều bản ghi vị thế, rồi chuyển sang CalculateAndPlaceSLTP().
    // =========================================================================
    private void ArmDebounceTimer()
    {
        lock (_lock)
        {
            _pendingFill = true;
            _debounceRetries = 0;
            // Dispose any existing timer before creating a new one (safety).
            // [VI] Hủy timer cũ (nếu có) trước khi tạo timer mới (an toàn).
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => OnDebounceFlush(), null, DebounceMs, Timeout.Infinite);
        }
    }

    private void OnDebounceFlush()
    {
        // Dispose the fired timer (but DO NOT clear _pendingFill yet — we may still be
        // waiting for a late broker confirmation and need the ghost-guard to stay armed).
        // [VI] Hủy timer vừa chạy (nhưng CHƯA xóa _pendingFill — có thể còn đang chờ sàn
        // [VI] xác nhận muộn, cần giữ cờ chống-vị-thế-lạ).
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        // If the trade was already closed or SL/TP already set, nothing to do.
        // [VI] Nếu lệnh đã đóng hoặc SL/TP đã đặt rồi thì không làm gì.
        if (!_inTrade || _slTPSet)
        {
            lock (_lock) { _pendingFill = false; }
            return;
        }

        // Query the platform for our open positions matching symbol, account, and direction.
        // [VI] Truy vấn sàn các vị thế đang mở khớp symbol, tài khoản và hướng.
        var positions = Core.Instance.Positions
            .Where(p => IsOurs(p.Symbol)
                     && IsOurs(p.Account)
                     && p.Side == (_isLong ? Side.Buy : Side.Sell))
            .ToList();

        // If no position exists yet, the broker may simply be confirming the fill LATER
        // than DebounceMs. Retry a few more times (keeping _pendingFill set so the
        // late-arriving position is NOT mistaken for an unmanaged "ghost") before giving up.
        // [VI] Nếu chưa có vị thế, có thể sàn xác nhận khớp MUỘN hơn DebounceMs. Thử lại
        // [VI] thêm vài lần (giữ _pendingFill để vị thế về trễ KHÔNG bị nhầm là vị thế "lạ")
        // [VI] trước khi bỏ cuộc.
        if (positions.Count == 0)
        {
            if (_debounceRetries < MaxDebounceRetries)
            {
                _debounceRetries++;
                Log(string.Format("[EN]   [DEBOUNCE] Fill not confirmed yet — waiting (retry {0}/{1})...", _debounceRetries, MaxDebounceRetries), StrategyLoggingLevel.Info);
                Log(string.Format("[VI]   [DEBOUNCE] Chưa xác nhận khớp — đang chờ (thử lại {0}/{1})...", _debounceRetries, MaxDebounceRetries), StrategyLoggingLevel.Info);
                lock (_lock)
                {
                    _debounceTimer?.Dispose();
                    _debounceTimer = new Timer(_ => OnDebounceFlush(), null, DebounceMs, Timeout.Infinite);
                }
                return;
            }

            // Exhausted all retries — the order really was not filled. Reset and wait.
            // [VI] Đã hết số lần thử — lệnh thật sự chưa khớp. Reset và chờ.
            Log("[EN]   [DEBOUNCE] Position not confirmed by broker after retries — resetting protective state.", StrategyLoggingLevel.Error);
            Log("[VI]   [DEBOUNCE] Vị thế chưa được sàn xác nhận sau nhiều lần thử — Reset trạng thái phòng vệ.", StrategyLoggingLevel.Error);
            lock (_lock) { _pendingFill = false; }
            _debounceRetries = 0;
            ResetTradeState();
            return;
        }

        // Position confirmed — clear the pending-fill guard and proceed to place SL/TP.
        // [VI] Đã xác nhận vị thế — xóa cờ chờ-khớp và tiến hành đặt SL/TP.
        lock (_lock) { _pendingFill = false; }
        _debounceRetries = 0;

        // Calculate the volume-weighted average fill price across all position records.
        // [VI] Tính giá khớp trung bình theo khối lượng (VWAP) qua mọi bản ghi vị thế.
        double qty = positions.Sum(p => p.Quantity);
        double vwap = positions.Sum(p => p.OpenPrice * p.Quantity) / qty;
        vwap = RoundToTick(vwap);

        _fillQty = qty;
        _entryPrice = vwap;

        Log(string.Format("[EN]   [DEBOUNCE] Entry filled! Actual VWAP fill price: {0:F2}. Calculating SL/TP...", vwap), StrategyLoggingLevel.Info);
        Log(string.Format("[VI]   [DEBOUNCE] Đã khớp Entry thực tế! Giá khớp VWAP: {0:F2}. Bắt đầu tính SL/TP...", vwap), StrategyLoggingLevel.Info);
        // Now that we know the real fill price, compute and place SL and TP.
        // [VI] Giờ đã biết giá khớp thật, tính và đặt SL và TP.
        CalculateAndPlaceSLTP(qty, vwap);
    }

    // =========================================================================
    //  HẬU TÍNH TOÁN VÀ ĐẶT LỆNH SL/TP
    //  (Post-fill SL/TP Calculation and Order Placement)
    //
    //  Called after the actual fill price is confirmed. Computes the exact
    //  SL/TP prices based on the real entry price, enforces the minimum SL
    //  distance floor (4 pts), places a Stop order for SL and a Limit order
    //  for TP, then optionally subscribes the trailing stop if enabled.
    // -------------------------------------------------------------------------
    //  [VI] HẬU TÍNH TOÁN VÀ ĐẶT LỆNH SL/TP
    //  [VI] Gọi sau khi xác nhận giá khớp thật. Tính chính xác giá SL/TP dựa
    //       trên giá vào thật, áp mức sàn khoảng cách SL tối thiểu (4 điểm),
    //       đặt lệnh Stop cho SL và lệnh Limit cho TP, sau đó đăng ký trailing
    //       stop nếu được bật.
    // =========================================================================
    private void CalculateAndPlaceSLTP(double qty, double fillPrice)
    {
        // Guard against running twice.
        // [VI] Canh không cho chạy hai lần.
        if (_slTPSet) return;

        // Distance from the actual fill price to the signal's raw SL candle level.
        // [VI] Khoảng cách từ giá khớp thật đến mức SL thô của nến tín hiệu.
        double calculatedDist = _isLong ? (fillPrice - _signalRawSLPrice) : (_signalRawSLPrice - fillPrice);

        // If the real SL distance exceeds MaxSLPoints and capping is allowed,
        // substitute the user's fixed cap distance instead.
        // [VI] Nếu khoảng cách SL thật vượt MaxSLPoints và được phép dùng trần,
        // [VI] thay bằng khoảng cách trần cố định của người dùng.
        if (calculatedDist > MaxSLPoints)
        {
            if (UseCappedSL)
            {
                calculatedDist = CappedSLPoints;
                Log(string.Format("[EN]   [SL] Distance exceeds limit. Forcing Capped SL to: {0} pts", CappedSLPoints), StrategyLoggingLevel.Info);
                Log(string.Format("[VI]   [SL] Khoảng cách vượt ngưỡng. Ép Capped SL về: {0} pts", CappedSLPoints), StrategyLoggingLevel.Info);
            }
        }

        // Enforce a minimum SL distance of 4 points to avoid orders too close to current price.
        // [VI] Áp khoảng cách SL tối thiểu 4 điểm để tránh đặt lệnh quá sát giá hiện tại.
        if (calculatedDist < 4.0)
        {
            calculatedDist = 4.0;
        }

        // Compute the final SL and TP prices rounded to the nearest tick.
        // [VI] Tính giá SL và TP cuối cùng, làm tròn về tick gần nhất.
        double sl = _isLong ? RoundToTick(fillPrice - calculatedDist) : RoundToTick(fillPrice + calculatedDist);
        double tp = _isLong ? RoundToTick(fillPrice + calculatedDist * tpMultiplierInternal) : RoundToTick(fillPrice - calculatedDist * tpMultiplierInternal);

        _slPrice = sl;
        _tpPrice = tp;
        _slDist = calculatedDist;

        // Exit orders are on the opposite side to the entry (sell to exit long, buy to exit short).
        // [VI] Lệnh thoát ở chiều ngược với lệnh vào (bán để thoát Long, mua để thoát Short).
        Side exitSide = _isLong ? Side.Sell : Side.Buy;

        // --- Risk-Amount safety trim (only when auto risk sizing is ON) ---
        // The position was sized at signal time using the candle close, but the
        // REAL risk depends on the actual fill price + final (capped) SL distance.
        // If the market filled away from the signal, the filled size can risk more
        // than RiskAmountUSD. Re-size against the real SL distance and, if we are
        // holding too many contracts, close the surplus before arming SL/TP so the
        // realised risk never exceeds the configured amount.
        // [VI] --- Cắt bớt contract để giữ rủi ro (chỉ khi bật tự tính theo $ rủi ro) ---
        // [VI] Vị thế được tính size lúc có tín hiệu dựa trên giá đóng nến, nhưng rủi
        // [VI] ro THẬT phụ thuộc vào giá khớp thật + khoảng cách SL cuối (đã trần).
        // [VI] Nếu lệnh khớp lệch xa tín hiệu, size đã khớp có thể rủi ro nhiều hơn
        // [VI] RiskAmountUSD. Tính lại size theo SL thật, và nếu đang giữ quá nhiều
        // [VI] contract thì đóng bớt phần thừa trước khi đặt SL/TP, để rủi ro thực
        // [VI] tế không bao giờ vượt mức đã cài.
        if (UseRiskAmount)
        {
            int targetQty = GetContractCount(calculatedDist);   // correct size for the REAL stop distance  // [VI] size đúng cho khoảng cách SL THẬT
            int filledQty = (int)qty;

            if (filledQty > targetQty)
            {
                int trimQty = filledQty - targetQty;

                var trimResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Symbol = CurrentSymbol,
                    Account = CurrentAccount,
                    Side = exitSide,                 // reduce the position (opposite side of entry)  // [VI] giảm vị thế (chiều ngược lệnh vào)
                    Quantity = trimQty,
                    OrderTypeId = OrderType.Market,
                    TimeInForce = TimeInForce.IOC,
                    Comment = (_isLong ? "Oneshot Long" : "Oneshot Short") + " RiskTrim"
                });

                if (trimResult.Status == TradingOperationResultStatus.Failure)
                {
                    // Trim failed: keep the full filled size and protect ALL of it.
                    // [VI] Cắt thất bại: giữ nguyên size đã khớp và bảo vệ TOÀN BỘ.
                    Log("[EN]   ⚠ [RISK TRIM] Trim failed: " + trimResult.Message + " — keeping the full filled size.", StrategyLoggingLevel.Error);
                    Log("[VI]   ⚠ [RISK TRIM] Đóng bớt thất bại: " + trimResult.Message + " — giữ nguyên size đã khớp.", StrategyLoggingLevel.Error);
                }
                else
                {
                    // Trim sent: SL/TP below will cover only the remaining target size.
                    // [VI] Đã gửi lệnh cắt: SL/TP bên dưới chỉ bảo vệ phần size còn lại.
                    Log(string.Format("[EN]   [RISK TRIM] Real SL {0:F1}pts → correct size {1} (filled {2}). Closing {3} contract(s) to keep risk ≤ ${4:F0}.",
                        calculatedDist, targetQty, filledQty, trimQty, RiskAmountUSD), StrategyLoggingLevel.Trading);
                    Log(string.Format("[VI]   [RISK TRIM] SL thật {0:F1}pts → size đúng {1} (đã khớp {2}). Đóng bớt {3} contract để giữ rủi ro ≤ ${4:F0}.",
                        calculatedDist, targetQty, filledQty, trimQty, RiskAmountUSD), StrategyLoggingLevel.Trading);
                    qty = targetQty;          // SL/TP protect only the kept contracts  // [VI] SL/TP chỉ bảo vệ số contract còn giữ
                    _fillQty = targetQty;     // keep status display in sync             // [VI] đồng bộ hiển thị trạng thái
                }
            }
        }

        // --- Place Stop Loss order ---
        // [VI] --- Đặt lệnh Stop Loss ---
        var slResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Symbol = CurrentSymbol,
            Account = CurrentAccount,
            Side = exitSide,
            Quantity = qty,
            OrderTypeId = OrderType.Stop,
            TriggerPrice = sl,
            TimeInForce = TimeInForce.GTC,
            Comment = _isLong ? "Oneshot Long SL" : "Oneshot Short SL"
        });

        // If SL fails to place, flatten immediately — we cannot hold an unprotected position.
        // [VI] Nếu đặt SL thất bại, đóng vị thế ngay — không thể giữ vị thế không được bảo vệ.
        if (slResult.Status == TradingOperationResultStatus.Failure)
        {
            Log("[EN]   [CRITICAL] Failed to place SL: " + slResult.Message + " — auto-flattening the position to protect capital!", StrategyLoggingLevel.Error);
            Log("[VI]   [CRITICAL] Đặt SL thất bại: " + slResult.Message + " — Tự động Flatten vị thế để bảo toàn vốn!", StrategyLoggingLevel.Error);
            FlattenPosition("SL placement failed");
            return;
        }
        _activeSLId = slResult.OrderId;

        // --- Place Take Profit order ---
        // [VI] --- Đặt lệnh Take Profit ---
        var tpResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Symbol = CurrentSymbol,
            Account = CurrentAccount,
            Side = exitSide,
            Quantity = qty,
            OrderTypeId = OrderType.Limit,
            Price = tp,
            TimeInForce = TimeInForce.GTC,
            Comment = _isLong ? "Oneshot Long TP" : "Oneshot Short TP"
        });

        // TP failure is non-critical (trade still has SL protection); log and continue.
        // [VI] TP lỗi không nghiêm trọng (lệnh vẫn còn SL bảo vệ); ghi log và tiếp tục.
        if (tpResult.Status == TradingOperationResultStatus.Failure)
        {
            Log("[EN]   ⚠ Failed to place TP: " + tpResult.Message, StrategyLoggingLevel.Error);
            Log("[VI]   ⚠ Đặt TP thất bại: " + tpResult.Message, StrategyLoggingLevel.Error);
        }
        else
            _activeTPId = tpResult.OrderId;

        _slTPSet = true;
        Log(string.Format("[EN]   [SL/TP PLACED] Entry:{0:F2} | SL:{1:F2} | TP:{2:F2} | SL distance:{3:F1}pts", fillPrice, sl, tp, calculatedDist), StrategyLoggingLevel.Trading);
        Log(string.Format("[VI]   [ĐẶT SL/TP THÀNH CÔNG] Vào:{0:F2} | SL:{1:F2} | TP:{2:F2} | Khoảng cách SL:{3:F1}pts", fillPrice, sl, tp, calculatedDist), StrategyLoggingLevel.Trading);

        // --- Telegram entry alert (Long/Short, size, stop trigger, take profit) ---
        // [VI] --- Cảnh báo Telegram khi vào lệnh (Long/Short, size, stop trigger, take profit) ---
        SendTelegram(string.Format(
            "{0} ENTRY {1}\n" +
            "Symbol: {2}\n" +
            "Order Size: {3} contract(s)\n" +
            "Entry: {4:F2}\n" +
            "Stop Trigger: {5:F2}\n" +
            "Take Profit: {6:F2}",
            _isLong ? "🟢" : "🔴",
            _isLong ? "LONG" : "SHORT",
            CurrentSymbol != null ? CurrentSymbol.Name : "?",
            (int)qty, fillPrice, sl, tp));

        // If trailing stop is enabled, subscribe to tick events to begin trail monitoring.
        // [VI] Nếu trailing stop được bật, đăng ký sự kiện tick để bắt đầu theo dõi trail.
        if (UseTrailingStop)
            SubscribeTrail();
    }

    // =========================================================================
    //  TRAILING STOP
    //  Tick-driven mechanism that moves the stop-loss in the profit direction
    //  as price advances, locking in gains.
    //
    //  SubscribeTrail()      — hooks into the symbol's NewLast tick event.
    //  UnsubscribeTrail()    — unhooks and resets all trail state.
    //  OnNewLastTrail()      — tick event handler; forwards to ProcessTrailTick
    //                          under a lock to prevent race conditions.
    //  ProcessTrailTick()    — evaluates each tick, activates the trail once
    //                          TrailTriggerPoints profit is reached, then
    //                          schedules a stop-move when improvement criteria
    //                          (distance, cooldown, minimum move) are all met.
    //  MoveTrailStopAsync()  — runs on a thread-pool thread: cancels the old
    //                          SL order and places a new one at the new level.
    //  TryCancelAndVerify()  — cancels an order and polls until confirmed gone
    //                          (up to TrailCancelVerifyMs) before returning.
    // -------------------------------------------------------------------------
    //  [VI] TRAILING STOP
    //  [VI] Cơ chế theo tick dời stop-loss theo hướng có lãi khi giá tiến tới,
    //       nhằm khóa lợi nhuận.
    //  [VI] SubscribeTrail()    — móc vào sự kiện tick NewLast của symbol.
    //  [VI] UnsubscribeTrail()  — gỡ móc và reset mọi trạng thái trail.
    //  [VI] OnNewLastTrail()    — bộ xử lý tick; chuyển sang ProcessTrailTick
    //                            trong vùng khóa để tránh tranh chấp luồng.
    //  [VI] ProcessTrailTick()  — đánh giá từng tick, kích hoạt trail khi đạt
    //                            lãi TrailTriggerPoints, rồi lên lịch dời stop
    //                            khi đủ điều kiện (khoảng cách, cooldown, mức tối thiểu).
    //  [VI] MoveTrailStopAsync()— chạy trên luồng thread-pool: hủy SL cũ và đặt
    //                            SL mới ở mức mới.
    //  [VI] TryCancelAndVerify()— hủy một lệnh và chờ xác nhận đã biến mất
    //                            (tối đa TrailCancelVerifyMs) trước khi trả về.
    // =========================================================================
    private void SubscribeTrail()
    {
        if (_trailSubscribed || CurrentSymbol == null) return;
        // Reset all trail state at the start of each new trade.
        // [VI] Reset mọi trạng thái trail khi bắt đầu mỗi lệnh mới.
        _trailActivated = false;
        _isMovingStop = false;
        _lastStopMoveTime = DateTime.MinValue;
        _lastTickPrice = double.NaN;
        CurrentSymbol.NewLast += OnNewLastTrail;
        _trailSubscribed = true;
    }

    private void UnsubscribeTrail()
    {
        if (!_trailSubscribed) return;
        if (CurrentSymbol != null) CurrentSymbol.NewLast -= OnNewLastTrail;
        _trailSubscribed = false;
        _trailActivated = false;
        _isMovingStop = false;
        _lastTickPrice = double.NaN;
    }

    private void OnNewLastTrail(Symbol sym, Last last)
    {
        double price = last.Price;
        // Ignore invalid ticks.
        // [VI] Bỏ qua các tick không hợp lệ.
        if (double.IsNaN(price) || price <= 0) return;
        // Only process ticks while we have an active, protected position.
        // [VI] Chỉ xử lý tick khi đang có vị thế hoạt động và đã được bảo vệ.
        if (!_inTrade || !_slTPSet) return;

        // Serialize tick processing to avoid concurrent stop-move operations.
        // [VI] Tuần tự hóa xử lý tick để tránh các thao tác dời stop đồng thời.
        lock (_lock) { ProcessTrailTick(price); }
    }

    private void ProcessTrailTick(double price)
    {
        // If a stop-move is already in flight, skip this tick.
        // [VI] Nếu đang có thao tác dời stop chạy dở, bỏ qua tick này.
        if (_isMovingStop) return;

        double tick = CurrentSymbol?.TickSize ?? 0.25;
        // Filter out sub-tick noise to avoid processing trivially small price changes.
        // [VI] Lọc nhiễu nhỏ hơn 1 tick để khỏi xử lý thay đổi giá quá nhỏ.
        if (!double.IsNaN(_lastTickPrice) && Math.Abs(price - _lastTickPrice) < tick * 0.5) return;
        _lastTickPrice = price;

        // Measure current unrealised profit in points.
        // [VI] Đo lãi chưa thực hiện hiện tại theo điểm.
        double profit = _isLong ? price - _entryPrice : _entryPrice - price;

        // --- Phase 1: Waiting for the trigger threshold ---
        // The trail does not activate until profit reaches TrailTriggerPoints.
        // [VI] --- Pha 1: Chờ đạt ngưỡng kích hoạt ---
        // [VI] Trail không kích hoạt cho đến khi lãi đạt TrailTriggerPoints.
        if (!_trailActivated)
        {
            if (profit >= TrailTriggerPoints)
            {
                _trailActivated = true;
                _isMovingStop = true;
                // Fire the first stop move on a background thread to avoid blocking the tick event.
                // [VI] Bắn lần dời stop đầu tiên trên luồng nền để không chặn sự kiện tick.
                double capturedPrice = price;
                ThreadPool.QueueUserWorkItem(_ => MoveTrailStopAsync(capturedPrice, skipGuard: true));
            }
            return;
        }

        // --- Phase 2: Trail is active — evaluate whether to move the stop ---
        // [VI] --- Pha 2: Trail đang hoạt động — đánh giá có nên dời stop không ---
        double newStop = _isLong ? RoundToTick(price - TrailOffsetPoints) : RoundToTick(price + TrailOffsetPoints);
        // Only move if the new stop is meaningfully better than the current one.
        // [VI] Chỉ dời nếu stop mới tốt hơn stop hiện tại một cách đáng kể.
        bool isBetter = double.IsNaN(_slPrice) || (_isLong ? newStop >= _slPrice + TrailMinMovePoints : newStop <= _slPrice - TrailMinMovePoints);

        if (!isBetter) return;
        // Respect the cooldown period between consecutive stop moves.
        // [VI] Tôn trọng khoảng cooldown giữa các lần dời stop liên tiếp.
        if ((DateTime.UtcNow - _lastStopMoveTime).TotalSeconds < TrailCooldownSeconds) return;

        // Schedule the stop move on a background thread.
        // [VI] Lên lịch dời stop trên luồng nền.
        _isMovingStop = true;
        double cp = price;
        ThreadPool.QueueUserWorkItem(_ => MoveTrailStopAsync(cp));
    }

    private void MoveTrailStopAsync(double price, bool skipGuard = false)
    {
        // Re-check the guard inside the lock unless we are the first move (skipGuard).
        // [VI] Kiểm tra lại cờ canh trong vùng khóa, trừ khi đây là lần dời đầu tiên (skipGuard).
        if (!skipGuard) { lock (_lock) { if (!_isMovingStop) return; } }

        try
        {
            if (!_inTrade || !_slTPSet) return;
            // Compute the new stop level at TrailOffsetPoints behind the current price.
            // [VI] Tính mức stop mới cách giá hiện tại TrailOffsetPoints (phía sau).
            double newStopPx = _isLong ? RoundToTick(price - TrailOffsetPoints) : RoundToTick(price + TrailOffsetPoints);

            // Only proceed if the new stop is actually an improvement over the current SL.
            // [VI] Chỉ tiếp tục nếu stop mới thực sự tốt hơn SL hiện tại.
            if (!double.IsNaN(_slPrice))
            {
                bool isImprovement = _isLong ? newStopPx > _slPrice : newStopPx < _slPrice;
                if (!isImprovement) return;
            }

            // Read the current SL order ID under the lock.
            // [VI] Đọc ID lệnh SL hiện tại trong vùng khóa.
            string oldSlId;
            lock (_lock) { oldSlId = _activeSLId; }

            // Cancel the existing SL order and wait for confirmation before placing the new one.
            // [VI] Hủy lệnh SL hiện có và chờ xác nhận trước khi đặt lệnh mới.
            if (oldSlId != null)
            {
                bool confirmed = TryCancelAndVerify(oldSlId);
                if (!confirmed) return;  // Cancel failed — abort to avoid losing protection.  // [VI] Hủy thất bại — dừng để không mất bảo vệ.
                lock (_lock) { if (_activeSLId == oldSlId) _activeSLId = null; }
            }

            // Place the replacement SL at the new (better) stop price.
            // [VI] Đặt lệnh SL thay thế ở mức stop mới (tốt hơn).
            var slResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = CurrentSymbol,
                Account = CurrentAccount,
                Side = _isLong ? Side.Sell : Side.Buy,
                Quantity = _fillQty,
                OrderTypeId = OrderType.Stop,
                TriggerPrice = newStopPx,
                TimeInForce = TimeInForce.GTC,
                Comment = _isLong ? "Oneshot Long TrailSL" : "Oneshot Short TrailSL"
            });

            lock (_lock)
            {
                if (slResult.Status == TradingOperationResultStatus.Failure) return;
                // Update the tracked SL order ID and price.
                // [VI] Cập nhật ID và giá của lệnh SL đang theo dõi.
                _activeSLId = slResult.OrderId;
                _slPrice = newStopPx;
                _lastStopMoveTime = DateTime.UtcNow;
                _trailMoveCount++;
            }
        }
        finally { lock (_lock) { _isMovingStop = false; } }  // Always release the guard.  // [VI] Luôn nhả cờ canh.
    }

    // Attempts to cancel an order and then polls for up to TrailCancelVerifyMs
    // to confirm it has been removed from the order book. Returns true if gone.
    // [VI] Thử hủy một lệnh rồi chờ tối đa TrailCancelVerifyMs để xác nhận nó đã
    // [VI] bị xóa khỏi sổ lệnh. Trả về true nếu đã biến mất.
    private bool TryCancelAndVerify(string orderId)
    {
        if (string.IsNullOrEmpty(orderId)) return true;
        var order = Core.Instance.Orders.FirstOrDefault(o => o.Id == orderId);
        if (order == null) return true;  // Already gone.  // [VI] Đã biến mất rồi.

        try
        {
            var result = Core.Instance.CancelOrder((IOrder)order);
            if (result.Status != TradingOperationResultStatus.Success) return false;
        }
        catch { return false; }

        // Poll until the order disappears or the timeout expires.
        // [VI] Hỏi vòng cho đến khi lệnh biến mất hoặc hết thời gian chờ.
        int elapsed = 0;
        const int pollInterval = 50;
        while (elapsed < TrailCancelVerifyMs)
        {
            Thread.Sleep(pollInterval);
            elapsed += pollInterval;
            if (!Core.Instance.Orders.Any(o => o.Id == orderId)) return true;
        }
        return false;
    }

    // =========================================================================
    //  EVENT: OnPositionAdded
    //  Fired by the platform whenever a new position opens on this account.
    //  If a position appears that we did not create (i.e., _inTrade is false),
    //  it is treated as a "ghost" position and immediately closed for safety.
    // -------------------------------------------------------------------------
    //  [VI] SỰ KIỆN: OnPositionAdded
    //  [VI] Sàn kích hoạt mỗi khi một vị thế mới mở trên tài khoản này.
    //  [VI] Nếu xuất hiện vị thế mà bot không tạo (tức _inTrade = false), nó
    //       bị coi là vị thế "lạ" (ghost) và đóng ngay để an toàn.
    // =========================================================================
    private void OnPositionAdded(Position position)
    {
        if (!IsOurs(position.Symbol) || !IsOurs(position.Account)) return;
        if (_flattening) return;
        // _inTrade  → this is our managed position.
        // _pendingFill → this is our own entry whose fill the broker is still confirming
        //   (it may arrive LATER than DebounceMs). Treat it as ours, not a ghost.
        // [VI] _inTrade  → đây là vị thế bot đang quản lý.
        // [VI] _pendingFill → đây là lệnh vào của chính bot mà sàn còn đang xác nhận khớp
        // [VI]   (có thể về MUỘN hơn DebounceMs). Coi là của bot, không phải vị thế lạ.
        if (_inTrade || _pendingFill) return;

        // Unknown / unmanaged position detected — flatten it immediately.
        // [VI] Phát hiện vị thế lạ / không quản lý — đóng ngay lập tức.
        _flattening = true;
        Log("[EN] [WARNING] Detected an unmanaged position not created by the bot — flattening to protect the account.", StrategyLoggingLevel.Error);
        Log("[VI] [CẢNH BÁO] Phát hiện vị thế lạ không do bot quản lý — Tiến hành Flatten bảo vệ tài khoản.", StrategyLoggingLevel.Error);
        FlattenPosition("Ghost position");
    }

    // =========================================================================
    //  EVENT: OnPositionRemoved
    //  Fired whenever a position closes (TP hit, SL hit, manual close, etc.).
    //  Handles three distinct scenarios:
    //   1. Flatten completed (not a normal trade close) → just reset state.
    //   2. Normal trade exit → cancel orphan orders and reset state.
    //   3. Pending reverse → cancel orphans, then immediately fire the new entry.
    // -------------------------------------------------------------------------
    //  [VI] SỰ KIỆN: OnPositionRemoved
    //  [VI] Kích hoạt mỗi khi một vị thế đóng (chạm TP, chạm SL, đóng tay,...).
    //  [VI] Xử lý ba tình huống riêng biệt:
    //   [VI] 1. Flatten xong (không phải đóng lệnh thường) → chỉ reset trạng thái.
    //   [VI] 2. Thoát lệnh bình thường → hủy lệnh mồ côi và reset trạng thái.
    //   [VI] 3. Đang chờ đảo chiều → hủy lệnh mồ côi, rồi bắn ngay lệnh mới.
    // =========================================================================
    private void OnPositionRemoved(Position position)
    {
        if (!IsOurs(position.Symbol) || !IsOurs(position.Account)) return;
        // Wait until ALL positions for this symbol/account are gone before acting.
        // [VI] Chờ đến khi TẤT CẢ vị thế của symbol/tài khoản này biến mất rồi mới hành động.
        bool stillOpen = Core.Instance.Positions.Any(p => IsOurs(p.Symbol) && IsOurs(p.Account));
        if (stillOpen) return;

        // --- Per-trade Profit/Loss report (bilingual) ---
        // [VI] --- Báo cáo Lãi/Lỗ mỗi lệnh (song ngữ) ---
        // Read the realised P/L straight from the just-closed position so you can
        // track each trade's result at a glance in the strategy log.
        // [VI] Đọc Lãi/Lỗ đã thực hiện ngay từ vị thế vừa đóng để bạn theo dõi
        // [VI] kết quả từng lệnh ngay trên log chiến lược.
        if (_inTrade)
            LogTradePnL(position);

        // Case 1: This was a defensive flatten (not an in-trade exit).
        // [VI] Trường hợp 1: Đây là flatten phòng vệ (không phải thoát lệnh đang chạy).
        if (_flattening && !_inTrade && !_pendingReverse)
        {
            ResetTradeState();
            return;
        }

        // Case 2: Normal trade exit (SL, TP, or manual).
        // [VI] Trường hợp 2: Thoát lệnh bình thường (SL, TP, hoặc đóng tay).
        if (_inTrade && !_pendingReverse)
        {
            CancelOrphanOrders();
            ResetTradeState();
            return;
        }

        // Case 3: The flatten was triggered to execute a reverse trade.
        // [VI] Trường hợp 3: Flatten được kích hoạt để thực hiện lệnh đảo chiều.
        if (_pendingReverse)
        {
            CancelOrphanOrders();
            // Capture reverse parameters before resetting state.
            // [VI] Lưu lại tham số đảo chiều trước khi reset trạng thái.
            bool flipIsLong = _reverseIsLong;
            double flipSL = _reverseSLPrice;
            double flipTP = _reverseTPPrice;
            double flipDist = _reverseSLDist;
            int flipQty = _reverseContracts;
            _pendingReverse = false;
            ResetTradeState();

            // Fire the reverse entry now that we are flat.
            // [VI] Bắn lệnh đảo chiều giờ đã không còn vị thế.
            PlaceEntry(flipIsLong ? Side.Buy : Side.Sell, flipSL, flipQty);
        }
    }

    // =========================================================================
    //  LogTradePnL
    //  Logs the realised Profit/Loss of a just-closed position in both English
    //  and Vietnamese: net P/L (after fees), gross P/L, and P/L in ticks, plus
    //  a WIN/LOSS tag — so each trade's outcome is easy to follow in the log.
    // -------------------------------------------------------------------------
    //  [VI] LogTradePnL
    //  [VI] Ghi log Lãi/Lỗ đã thực hiện của vị thế vừa đóng bằng cả tiếng Anh
    //       và tiếng Việt: Lãi/Lỗ ròng (sau phí), Lãi/Lỗ gộp, và Lãi/Lỗ theo
    //       số tick, kèm nhãn THẮNG/THUA — để dễ theo dõi kết quả từng lệnh.
    // =========================================================================
    private void LogTradePnL(Position position)
    {
        try
        {
            // Use the realized P/L accumulated from the closing fills (OnTradeAdded).
            // Do NOT read Position.NetPnL here — on a just-removed position that value
            // can be a stale UNREALIZED figure (it once reported a loss on a winner).
            // [VI] Dùng Lãi/Lỗ thực hiện đã cộng dồn từ các fill đóng (OnTradeAdded).
            // [VI] KHÔNG đọc Position.NetPnL ở đây — với vị thế vừa gỡ, giá trị đó có
            // [VI] thể là P/L CHƯA thực hiện cũ/đọng (từng báo lỗ cho một lệnh thắng).
            double netPnl = _tradeRealizedNet;
            double grossPnl = _tradeRealizedGross;
            double exitPx = _tradeExitPrice;

            // Fallback (rare event-ordering case where no closing fill was captured):
            // estimate gross from entry vs the position's current price.
            // [VI] Dự phòng (hiếm — khi chưa bắt được fill đóng do thứ tự sự kiện):
            // [VI] ước tính P/L gộp từ giá vào so với giá hiện tại của vị thế.
            if (!_tradeClosingSeen)
            {
                exitPx = position != null ? position.CurrentPrice : _entryPrice;
                double pv = PointValuePerContract();
                grossPnl = (_isLong ? (exitPx - _entryPrice) : (_entryPrice - exitPx)) * _fillQty * pv;
                netPnl = grossPnl;
            }

            string dir = _isLong ? "LONG" : "SHORT";

            // WIN if net P/L >= 0, otherwise LOSS. One tag per language.
            // [VI] THẮNG nếu Lãi/Lỗ ròng >= 0, ngược lại THUA. Mỗi ngôn ngữ một nhãn.
            string outcomeEn = netPnl >= 0 ? "WIN ✅" : "LOSS ❌";
            string outcomeVi = netPnl >= 0 ? "THẮNG ✅" : "THUA ❌";

            // English line.
            // [VI] Dòng tiếng Anh.
            Log(string.Format("[EN]   [TRADE CLOSED] {0} {1} | Entry:{2:F2} → Exit:{3:F2} | Net P/L: {4:F2} | Gross: {5:F2} | {6}",
                dir, _fillQty, _entryPrice, exitPx, netPnl, grossPnl, outcomeEn), StrategyLoggingLevel.Trading);

            // Vietnamese line.
            // [VI] Dòng tiếng Việt.
            Log(string.Format("[VI]   [ĐÓNG LỆNH] {0} {1} | Vào:{2:F2} → Ra:{3:F2} | Lãi/Lỗ ròng: {4:F2} | Gộp: {5:F2} | {6}",
                dir, _fillQty, _entryPrice, exitPx, netPnl, grossPnl, outcomeVi), StrategyLoggingLevel.Trading);

            // --- Telegram close alert (TP reached / Stop triggered + profit or loss in $) ---
            // Decide which exit fired by which target the exit price landed nearest to.
            // A trailing stop that closes in profit correctly shows "Stop Triggered".
            // [VI] --- Cảnh báo Telegram khi đóng lệnh (chạm TP / dính Stop + Lãi/Lỗ $) ---
            // [VI] Xác định loại thoát theo việc giá ra gần mức nào hơn. Trailing stop
            // [VI] đóng có lãi vẫn hiển thị đúng là "Stop Triggered".
            bool tpReached = Math.Abs(exitPx - _tpPrice) <= Math.Abs(exitPx - _slPrice);
            string exitLabel = tpReached ? "🎯 TAKE PROFIT REACHED" : "🛑 STOP TRIGGERED";
            string pnlLabel  = netPnl >= 0
                ? string.Format("✅ PROFIT: ${0:F2}", netPnl)
                : string.Format("❌ LOSS: -${0:F2}", Math.Abs(netPnl));

            SendTelegram(string.Format(
                "{0}\n" +
                "Symbol: {1}\n" +
                "{2} {3} contract(s)\n" +
                "Entry: {4:F2} → Exit: {5:F2}\n" +
                "{6}",
                exitLabel,
                CurrentSymbol != null ? CurrentSymbol.Name : "?",
                dir, (int)_fillQty, _entryPrice, exitPx, pnlLabel));
        }
        catch (Exception ex)
        {
            // Never let P/L logging break the close handling.
            // [VI] Không bao giờ để việc ghi log Lãi/Lỗ làm hỏng xử lý đóng lệnh.
            Log("[EN]   [P/L] Could not read the trade's profit/loss: " + ex.Message, StrategyLoggingLevel.Error);
            Log("[VI]   [P/L] Không đọc được Lãi/Lỗ của lệnh: " + ex.Message, StrategyLoggingLevel.Error);
        }
    }

    // =========================================================================
    //  TELEGRAM ALERTS
    //  A shared HttpClient and a fire-and-forget sender used to push trade
    //  notifications to a Telegram bot. The send runs on a background task so a
    //  slow or unreachable network can never block or delay trading logic.
    //  All sends are no-ops unless UseTelegramAlerts is ON and both the bot
    //  token and chat ID are filled in.
    // -------------------------------------------------------------------------
    //  [VI] CẢNH BÁO TELEGRAM
    //  [VI] Một HttpClient dùng chung và hàm gửi "bắn rồi quên" để đẩy thông báo
    //       lệnh tới bot Telegram. Việc gửi chạy ở luồng nền nên mạng chậm/lỗi
    //       không bao giờ chặn hay làm trễ logic giao dịch. Mọi lần gửi bị bỏ qua
    //       nếu chưa BẬT UseTelegramAlerts hoặc thiếu token/Chat ID.
    // =========================================================================
    // One reusable HttpClient for the whole strategy lifetime (static so it is
    // never disposed/recreated per message — the recommended HttpClient usage).
    // [VI] Một HttpClient tái dùng cho cả vòng đời chiến lược (static để không bị
    // [VI] hủy/tạo lại mỗi tin — cách dùng HttpClient được khuyến nghị).
    private static readonly HttpClient _telegramHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    private void SendTelegram(string message)
    {
        try
        {
            // Skip entirely when the feature is off or not configured.
            // [VI] Bỏ qua hoàn toàn khi tính năng tắt hoặc chưa cấu hình.
            if (!UseTelegramAlerts) return;
            if (string.IsNullOrWhiteSpace(TelegramBotToken) || string.IsNullOrWhiteSpace(TelegramChatId))
            {
                Log("[TELEGRAM] Alerts are ON but Bot Token / Chat ID is empty — skipping send.", StrategyLoggingLevel.Error);
                return;
            }

            string token  = TelegramBotToken.Trim();
            string chatId = TelegramChatId.Trim();
            string url = string.Format(
                "https://api.telegram.org/bot{0}/sendMessage?chat_id={1}&text={2}",
                token, Uri.EscapeDataString(chatId), Uri.EscapeDataString(message));

            // Fire-and-forget: never await on the trading thread.
            // [VI] Bắn rồi quên: không bao giờ chờ trên luồng giao dịch.
            Task.Run(async () =>
            {
                try
                {
                    var resp = await _telegramHttp.GetAsync(url).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Telegram returns a JSON body explaining the exact reason
                        // (e.g. "chat not found", "bot was blocked by the user").
                        // [VI] Telegram trả về JSON nêu rõ lý do (vd: "chat not found",
                        // [VI] "bot was blocked by the user").
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Log(string.Format("[TELEGRAM] Send returned HTTP {0} — {1}", (int)resp.StatusCode, body), StrategyLoggingLevel.Error);
                    }
                }
                catch (Exception ex)
                {
                    Log("[TELEGRAM] Send failed: " + ex.Message, StrategyLoggingLevel.Error);
                }
            });
        }
        catch (Exception ex)
        {
            // Telegram problems must never disturb trading.
            // [VI] Sự cố Telegram không bao giờ được làm phiền giao dịch.
            Log("[TELEGRAM] " + ex.Message, StrategyLoggingLevel.Error);
        }
    }

    // =========================================================================
    //  PointValuePerContract
    //  Returns the $ value of a 1.0 price move per contract for the current
    //  symbol (tickCost / tickSize). Falls back to a $0.50/tick assumption if
    //  the symbol's tick metadata is unavailable. Used only by the P/L fallback.
    // -------------------------------------------------------------------------
    //  [VI] PointValuePerContract (GIÁ TRỊ $ MỖI ĐIỂM / CONTRACT)
    //  [VI] Trả về giá trị $ của 1.0 điểm giá mỗi contract của symbol hiện tại
    //  [VI] (tickCost / tickSize). Dự phòng giả định $0.50/tick nếu thiếu dữ liệu
    //  [VI] tick. Chỉ dùng cho phần dự phòng tính P/L.
    // =========================================================================
    private double PointValuePerContract()
    {
        try
        {
            if (CurrentSymbol != null)
            {
                double price = CurrentSymbol.Last;
                if (price <= 0) price = CurrentSymbol.Bid;
                if (price <= 0) price = CurrentSymbol.Ask;
                var vt = CurrentSymbol.FindVariableTick(price);
                if (vt.TickSize > 0 && vt.TickCost > 0)
                    return vt.TickCost / vt.TickSize;
            }
        }
        catch { }
        double ts = CurrentSymbol?.TickSize ?? 0.25;
        if (ts <= 0) ts = 0.25;
        return 0.5 / ts;
    }

    // =========================================================================
    //  EVENT: OnOrderRemoved
    //  Fired when any order is removed from the platform (filled, cancelled, etc.).
    //  Clears _activeSLId or _activeTPId so we know those slots are free.
    // -------------------------------------------------------------------------
    //  [VI] SỰ KIỆN: OnOrderRemoved
    //  [VI] Kích hoạt khi một lệnh bị xóa khỏi nền tảng (đã khớp, đã hủy,...).
    //  [VI] Xóa _activeSLId hoặc _activeTPId để biết các vị trí đó đã trống.
    // =========================================================================
    private void OnOrderRemoved(Order order)
    {
        if (!IsOurs(order.Symbol) || !IsOurs(order.Account)) return;
        if (!_inTrade) return;
        if (order.Id == _activeSLId) _activeSLId = null;
        else if (order.Id == _activeTPId) _activeTPId = null;
    }

    // =========================================================================
    //  EVENT: OnTradeAdded
    //  Fires for every execution (fill). We accumulate the realized P/L of the
    //  CLOSING fills (opposite side to our entry: SL, TP, trim, reverse, manual)
    //  so OnPositionRemoved can report an accurate per-trade profit/loss. This
    //  is the reliable realized P/L source — Position.NetPnL at removal time can
    //  be a stale UNREALIZED value (it once reported a loss on a winning trade).
    // -------------------------------------------------------------------------
    //  [VI] SỰ KIỆN: OnTradeAdded
    //  [VI] Kích hoạt cho mỗi lần khớp (fill). Bot cộng dồn Lãi/Lỗ thực hiện của
    //  [VI] các fill ĐÓNG (chiều ngược lệnh vào: SL, TP, trim, đảo chiều, đóng tay)
    //  [VI] để OnPositionRemoved báo Lãi/Lỗ từng lệnh chính xác. Đây là nguồn P/L
    //  [VI] thực hiện đáng tin — Position.NetPnL lúc gỡ vị thế có thể bị sai/đọng.
    // =========================================================================
    private void OnTradeAdded(Trade trade)
    {
        if (trade == null || !IsOurs(trade.Symbol) || !IsOurs(trade.Account)) return;
        if (!_inTrade) return;

        // Closing fills are on the opposite side to the entry.
        // [VI] Các fill đóng nằm ở chiều ngược với lệnh vào.
        Side closingSide = _isLong ? Side.Sell : Side.Buy;
        if (trade.Side != closingSide) return;

        // Accumulate the platform's realized P/L for each closing fill.
        // [VI] Cộng dồn Lãi/Lỗ thực hiện (do sàn tính) của mỗi fill đóng.
        try
        {
            if (trade.NetPnl != null) _tradeRealizedNet += trade.NetPnl.Value;
            if (trade.GrossPnl != null) _tradeRealizedGross += trade.GrossPnl.Value;
        }
        catch { }
        _tradeExitPrice = trade.Price;
        _tradeClosingSeen = true;
    }

    // =========================================================================
    //  WATCHDOG
    //  Fires every WatchdogIntervalMs (30 s). Two responsibilities:
    //  1. While loading — reports progress towards the required bar count.
    //  2. While running — detects a stale feed (no new bar for FeedStaleSec)
    //     and rebuilds the historical data subscription to recover connectivity.
    //     Also logs a heartbeat with key runtime metrics.
    // -------------------------------------------------------------------------
    //  [VI] WATCHDOG (CANH GÁC)
    //  [VI] Chạy mỗi WatchdogIntervalMs (30 giây). Hai nhiệm vụ:
    //  [VI] 1. Khi đang nạp — báo tiến độ tới số nến cần thiết.
    //  [VI] 2. Khi đang chạy — phát hiện nguồn treo (không có nến mới trong
    //         FeedStaleSec) và dựng lại đăng ký dữ liệu lịch sử để khôi phục
    //         kết nối. Đồng thời ghi nhịp tim với các chỉ số chạy chính.
    // =========================================================================
    // =========================================================================
    //  IsOurs — identity-based matching for positions / orders / trades.
    //  Compares against the identity captured at OnRun (_symbolId/_accountId),
    //  NOT the live CurrentSymbol/CurrentAccount reference. If Quantower
    //  transiently reassigns CurrentSymbol to the wrong instrument on a
    //  disconnect, our real positions/orders are still matched correctly.
    // -------------------------------------------------------------------------
    //  [VI] IsOurs — so khớp theo DANH TÍNH lưu lúc OnRun (_symbolId/_accountId),
    //  [VI] KHÔNG so với con trỏ CurrentSymbol/CurrentAccount hiện tại. Nếu
    //  [VI] Quantower tạm đổi CurrentSymbol sang instrument sai khi mất kết nối,
    //  [VI] vị thế/lệnh thật của ta vẫn khớp đúng.
    // =========================================================================
    private bool IsOurs(Symbol s)  => s != null && _symbolId  != null && s.Id == _symbolId;
    private bool IsOurs(Account a) => a != null && _accountId != null && a.Id == _accountId;

    // =========================================================================
    //  TryResolveLiveSymbol
    //  Re-resolves CurrentSymbol against the LIVE broker connection after a
    //  disconnect. Returns true only when the original connection is back up AND
    //  a fresh, real symbol on that exact connection is available; otherwise it
    //  leaves CurrentSymbol untouched so the caller can retry next tick.
    // -------------------------------------------------------------------------
    //  [VI] TryResolveLiveSymbol — tìm lại CurrentSymbol trên kết nối broker ĐANG
    //  [VI] SỐNG sau khi mất kết nối. Chỉ trả về true khi kết nối gốc đã lên LẠI
    //  [VI] VÀ có symbol thật/mới đúng kết nối đó; nếu chưa thì giữ nguyên.
    // =========================================================================
    private bool TryResolveLiveSymbol()
    {
        // The original broker connection must be fully connected again first.
        // [VI] Kết nối broker gốc phải kết nối lại hoàn toàn trước đã.
        var conn = Core.Instance.Connections.Connected
            .FirstOrDefault(c => c.Id == _connectionId);
        if (conn == null) return false;

        // Find a fresh symbol pinned to that EXACT connection — never a same-named
        // instrument from another data feed. Skip "Fake" (placeholder) objects.
        // [VI] Tìm symbol mới ghim đúng kết nối ĐÓ — không vớ symbol cùng tên ở
        // [VI] nguồn khác. Bỏ qua object "Fake" (placeholder).
        var fresh = Core.Instance.Symbols.FirstOrDefault(s =>
            s != null && s.ConnectionId == _connectionId && s.State != BusinessObjectState.Fake &&
            (s.Id == _symbolId || s.Name == _symbolName));
        if (fresh == null) return false;

        CurrentSymbol = fresh;
        return true;
    }

    // =========================================================================
    //  RebuildFeed
    //  Re-resolves a fresh live symbol and rebuilds the historical-data feed +
    //  EMA on it, moving the trailing-stop subscription across. Safe to call
    //  repeatedly: it logs and returns (leaving everything untouched) until the
    //  broker connection is back and the symbol is available again.
    // -------------------------------------------------------------------------
    //  [VI] RebuildFeed — tìm lại symbol live mới, dựng lại nguồn dữ liệu + EMA,
    //  [VI] chuyển subscription trailing-stop sang. Gọi lại nhiều lần vẫn an toàn:
    //  [VI] nếu chưa khôi phục được thì log rồi return, giữ nguyên mọi thứ.
    // =========================================================================
    private void RebuildFeed(string reason)
    {
        Log(string.Format("[EN] [WATCHDOG] Rebuilding data feed — {0}", reason), StrategyLoggingLevel.Error);
        Log(string.Format("[VI] [WATCHDOG] Đang dựng lại nguồn dữ liệu — {0}", reason), StrategyLoggingLevel.Error);

        // Re-resolve the symbol against the LIVE broker connection. If the
        // connection is not back yet (or the symbol not republished), wait for
        // the next watchdog tick rather than rebuilding on a dead reference
        // (which would silently pull no data, or grab a same-named symbol from
        // the wrong connection).
        // [VI] Tìm lại symbol trên kết nối broker ĐANG SỐNG. Nếu kết nối chưa lên
        // [VI] lại (hoặc symbol chưa xuất hiện lại), chờ vòng watchdog sau thay vì
        // [VI] dựng feed trên con trỏ chết.
        if (!TryResolveLiveSymbol())
        {
            Log("[EN] [WATCHDOG] Broker connection / symbol not restored yet — will retry next tick.", StrategyLoggingLevel.Error);
            Log("[VI] [WATCHDOG] Kết nối broker / symbol chưa khôi phục — sẽ thử lại ở vòng sau.", StrategyLoggingLevel.Error);
            return;
        }

        // The trail tick handler is hooked onto the OLD symbol object. Move it to
        // the freshly resolved one (no-op if not subscribed).
        // [VI] Bộ xử lý tick trail đang gắn vào object symbol CŨ. Chuyển nó sang
        // [VI] object mới vừa tìm được (bỏ qua nếu chưa subscribe).
        bool trailWasSubscribed = _trailSubscribed;
        if (trailWasSubscribed) UnsubscribeTrail();

        // Dispose the stale feed and request a fresh one from the live symbol.
        // [VI] Hủy nguồn cũ và yêu cầu nguồn mới từ symbol đang sống.
        try { _hdm?.Dispose(); } catch { }

        _hdm = CurrentSymbol.GetHistory(CurrentPeriod, HistoryType.Last, Math.Max(500, EmaPeriod * 6));
        _ema = Core.Indicators.BuiltIn.EMA(EmaPeriod, PriceType.Close);
        _hdm.AddIndicator(_ema);

        if (trailWasSubscribed) SubscribeTrail();

        // Reset warmup flags so the new feed re-populates the zone lists. No
        // trading happens until warmup completes on the correct instrument.
        // [VI] Reset cờ warmup để nguồn mới dựng lại danh sách vùng. Không giao
        // [VI] dịch cho tới khi warmup xong trên đúng instrument.
        _warmedUp = false;
        _warmupAttempted = false;
        _lastProcessedBar = DateTime.MinValue;
        _lastHdmBarTime = DateTime.UtcNow;
        Log(string.Format("[EN] [WATCHDOG] ✔ Feed rebuilt on live connection — symbol '{0}'.", CurrentSymbol.Name), StrategyLoggingLevel.Info);
        Log(string.Format("[VI] [WATCHDOG] ✔ Đã dựng lại nguồn trên kết nối sống — symbol '{0}'.", CurrentSymbol.Name), StrategyLoggingLevel.Info);
    }

    private void OnWatchdog()
    {
        // During warmup, just report how many bars have loaded so far.
        // [VI] Khi warmup, chỉ báo đã nạp được bao nhiêu nến.
        if (!_warmedUp)
        {
            int currentCount = _hdm?.Count ?? 0;
            int requiredCount = EmaPeriod + 5;
            Log(string.Format("[EN] ♥ WATCHDOG – Waiting for session sync: {0}/{1} bars.", currentCount, requiredCount), StrategyLoggingLevel.Info);
            Log(string.Format("[VI] ♥ WATCHDOG – Bot đang đợi đồng bộ phiên: {0}/{1} nến.", currentCount, requiredCount), StrategyLoggingLevel.Info);
            return;
        }

        // --- FAST PATH ---
        // React within one watchdog interval (~30s) when the broker connection
        // has dropped, or when Quantower has reassigned CurrentSymbol to the
        // wrong / placeholder instrument on a disconnect (observed in the
        // settings UI). This closes the window where the bot could otherwise act
        // on the wrong symbol while waiting out the long stale timer.
        // [VI] --- ĐƯỜNG PHẢN ỨNG NHANH ---
        // [VI] Phản ứng ngay trong một chu kỳ watchdog (~30s) khi kết nối broker
        // [VI] bị rớt, hoặc khi Quantower đổi CurrentSymbol sang instrument sai/
        // [VI] placeholder lúc mất kết nối (đã thấy trong UI settings).
        bool brokerConnected = Core.Instance.Connections.Connected.Any(c => c.Id == _connectionId);
        bool symbolWrong = CurrentSymbol == null
                        || CurrentSymbol.State == BusinessObjectState.Fake
                        || CurrentSymbol.Id != _symbolId
                        || CurrentSymbol.ConnectionId != _connectionId;
        if (!brokerConnected || symbolWrong)
        {
            Log(string.Format("[EN] [WATCHDOG] ▲ Connection/symbol problem detected (brokerConnected={0}, symbolWrong={1}) — recovering...", brokerConnected, symbolWrong), StrategyLoggingLevel.Error);
            Log(string.Format("[VI] [WATCHDOG] ▲ Phát hiện sự cố kết nối/symbol (brokerConnected={0}, symbolWrong={1}) — đang khôi phục...", brokerConnected, symbolWrong), StrategyLoggingLevel.Error);
            RebuildFeed("connection lost or symbol reassigned on reconnect");
            return;
        }

        // --- SLOW PATH ---
        // Connection looks fine but the feed has been silent for too long
        // (e.g., a data glitch). Rebuild after FeedStaleSec as a backstop.
        // [VI] --- ĐƯỜNG CHẬM ---
        // [VI] Kết nối vẫn ổn nhưng nguồn im lặng quá lâu (vd lỗi data). Dựng lại
        // [VI] sau FeedStaleSec như một lớp dự phòng.
        if (_lastHdmBarTime != DateTime.MinValue)
        {
            double staleSec = (DateTime.UtcNow - _lastHdmBarTime).TotalSeconds;
            if (staleSec > FeedStaleSec)
            {
                RebuildFeed(string.Format("no new bar for {0:F0}s", staleSec));
                return;
            }
        }

        // Normal heartbeat log — shows bot health at a glance.
        // [VI] Log nhịp tim bình thường — cho thấy sức khỏe bot trong nháy mắt.
        double secondsSinceBar = _lastBarTime == DateTime.MinValue ? -1 : (DateTime.UtcNow - _lastBarTime).TotalSeconds;
        DateTime cutoff = DateTime.UtcNow.AddSeconds(-60);
        while (_tradeTimestamps.Count > 0 && _tradeTimestamps.Peek() < cutoff) _tradeTimestamps.Dequeue();

        Log(string.Format("[EN] ♥ ALIVE — Last bar {0:F1}s ago | EMA:{1:F2} | {2} | Trades/min:{3}/{4}",
            secondsSinceBar < 0 ? 0 : secondsSinceBar,
            double.IsNaN(_lastEmaVal) ? 0 : _lastEmaVal,
            _inTrade ? "IN TRADE" : "Watching...",
            _tradeTimestamps.Count, MaxTradesPerMinute), StrategyLoggingLevel.Info);
        Log(string.Format("[VI] ♥ HOẠT ĐỘNG — Nến cuối {0:F1}s trước | EMA:{1:F2} | {2} | Lệnh/phút:{3}/{4}",
            secondsSinceBar < 0 ? 0 : secondsSinceBar,
            double.IsNaN(_lastEmaVal) ? 0 : _lastEmaVal,
            _inTrade ? "ĐANG CÓ LỆNH" : "Đang theo dõi...",
            _tradeTimestamps.Count, MaxTradesPerMinute), StrategyLoggingLevel.Info);
    }

    // =========================================================================
    //  FlattenPosition
    //  Sends an opposite-side Market IOC order for every open position on
    //  this symbol/account, effectively closing everything immediately.
    //  Orphan SL/TP orders are cancelled first to avoid double-execution.
    // -------------------------------------------------------------------------
    //  [VI] FlattenPosition (ĐÓNG TOÀN BỘ VỊ THẾ)
    //  [VI] Gửi lệnh Market IOC chiều ngược cho mọi vị thế đang mở của symbol/
    //       tài khoản này, đóng tất cả ngay lập tức.
    //  [VI] Lệnh SL/TP mồ côi được hủy trước để tránh khớp hai lần.
    // =========================================================================
    private void FlattenPosition(string reason)
    {
        CancelOrphanOrders();
        foreach (var pos in Core.Instance.Positions.Where(p => IsOurs(p.Symbol) && IsOurs(p.Account)).ToList())
        {
            Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = CurrentSymbol,
                Account = CurrentAccount,
                Side = pos.Side == Side.Buy ? Side.Sell : Side.Buy,
                Quantity = pos.Quantity,
                OrderTypeId = OrderType.Market,
                TimeInForce = TimeInForce.IOC,
                Comment = "Oneshot Flatten"
            });
        }
        // Only reset state immediately if no reverse or ghost-flatten is in progress.
        // [VI] Chỉ reset trạng thái ngay nếu không đang đảo chiều hoặc flatten vị thế lạ.
        if (!_pendingReverse && !_flattening) ResetTradeState();
    }

    // =========================================================================
    //  CancelOrphanOrders
    //  Cancels the tracked SL and TP orders, then sweeps ALL remaining open
    //  orders with the "Oneshot" comment prefix (belt-and-suspenders cleanup).
    // -------------------------------------------------------------------------
    //  [VI] CancelOrphanOrders (HỦY LỆNH MỒ CÔI)
    //  [VI] Hủy các lệnh SL và TP đang theo dõi, rồi quét HỦY mọi lệnh còn mở
    //       có tiền tố comment "Oneshot" (dọn dẹp kỹ lưỡng cho chắc).
    // =========================================================================
    private void CancelOrphanOrders()
    {
        TryCancelById(_activeSLId); TryCancelById(_activeTPId);
        _activeSLId = null; _activeTPId = null;
        foreach (var o in Core.Instance.Orders.Where(o => IsOurs(o.Symbol) && IsOurs(o.Account) && o.Comment != null && o.Comment.StartsWith("Oneshot")).ToList())
            TryCancelById(o.Id);
    }

    // Looks up an order by ID and attempts to cancel it; silently swallows any errors.
    // [VI] Tra cứu lệnh theo ID và thử hủy; nuốt mọi lỗi một cách im lặng.
    private void TryCancelById(string orderId)
    {
        if (string.IsNullOrEmpty(orderId)) return;
        var order = Core.Instance.Orders.FirstOrDefault(o => o.Id == orderId);
        if (order == null) return;
        try { Core.Instance.CancelOrder((IOrder)order); } catch { }
    }

    // =========================================================================
    //  ResetTradeState
    //  Zeroes out all trade-related fields so the strategy is ready for the
    //  next signal. Also unsubscribes the trailing stop tick feed and disposes
    //  any pending debounce timer.
    // -------------------------------------------------------------------------
    //  [VI] ResetTradeState (RESET TRẠNG THÁI LỆNH)
    //  [VI] Đưa mọi trường liên quan đến lệnh về 0 để chiến lược sẵn sàng cho
    //       tín hiệu kế tiếp. Cũng gỡ đăng ký nguồn tick trailing và hủy timer
    //       debounce đang chờ (nếu có).
    // =========================================================================
    private void ResetTradeState()
    {
        UnsubscribeTrail();
        lock (_lock) { _debounceTimer?.Dispose(); _debounceTimer = null; _pendingFill = false; _debounceRetries = 0; }
        _inTrade = false; _isLong = false; _flattening = false;
        _entryPrice = 0; _slPrice = 0; _tpPrice = 0; _slDist = 0; _fillQty = 0;
        _slTPSet = false; _activeSLId = null; _activeTPId = null; _pendingReverse = false;
        _signalRawSLPrice = double.NaN; _trailMoveCount = 0;
        // Clear realized-P/L accumulators so the next trade starts fresh.
        // [VI] Xóa bộ cộng dồn Lãi/Lỗ để lệnh kế tiếp bắt đầu từ 0.
        _tradeRealizedNet = 0.0; _tradeRealizedGross = 0.0; _tradeExitPrice = 0.0; _tradeClosingSeen = false;
    }

    // =========================================================================
    //  GetContractCount
    //  Returns the number of contracts to trade.
    //
    //  Priority order:
    //   1. UseRiskAmount = true  → auto-size by dollar risk (any instrument):
    //        contracts = floor(RiskAmountUSD / dollarRiskPerContract)
    //        dollarRiskPerContract = (slDist / tickSize) × tickCost
    //        tickSize & tickCost are read from the symbol's own tick table
    //        (Symbol.FindVariableTick), so the per-tick $ value is detected
    //        automatically for futures, micros, stocks, FX, etc.
    //        Result is always floored (never round up), minimum 1.
    //   2. Default → return the fixed NumContracts value.
    // -------------------------------------------------------------------------
    //  [VI] GetContractCount (TÍNH SỐ CONTRACT)
    //  [VI] Trả về số contract sẽ giao dịch.
    //  [VI] Thứ tự ưu tiên:
    //   [VI] 1. UseRiskAmount = true → tự tính theo $ rủi ro (mọi công cụ):
    //        [VI] số contract = làm tròn xuống( RiskAmountUSD / rủi-ro-$-mỗi-contract )
    //        [VI] rủi-ro-$-mỗi-contract = (slDist / tickSize) × tickCost
    //        [VI] tickSize & tickCost đọc từ bảng tick của chính symbol
    //             (Symbol.FindVariableTick), nên giá trị $/tick được nhận diện
    //             tự động cho futures, micro, cổ phiếu, FX, v.v.
    //        [VI] Kết quả luôn làm tròn XUỐNG (không bao giờ làm tròn lên), tối thiểu 1.
    //   [VI] 2. Mặc định → trả về giá trị NumContracts cố định.
    // =========================================================================
    private int GetContractCount(double slDist)
    {
        // --- Mode 1: Risk-Amount sizing (auto-detect contracts for ANY instrument) ---
        // [VI] --- Chế độ 1: Tính size theo $ rủi ro (tự nhận diện contract cho MỌI công cụ) ---
        if (UseRiskAmount)
        {
            // Guard against zero or invalid SL distance.
            // [VI] Canh chống khoảng cách SL bằng 0 hoặc không hợp lệ.
            if (slDist <= 0) return 1;

            // Dollar risk of holding ONE contract through this stop distance:
            //   dollarRiskPerContract = (slDist / tickSize) × tickCost
            // tickCost is the real money value of one tick for THIS symbol, taken
            // from Quantower's per-symbol tick table — so it is correct for every
            // instrument (ES, MES, NQ, stocks, FX, …) with no hard-coded values.
            // [VI] Rủi ro $ khi giữ MỘT contract qua khoảng cách stop này:
            // [VI]   rủi-ro-$-mỗi-contract = (slDist / tickSize) × tickCost
            // [VI] tickCost là giá trị tiền thật của 1 tick cho CHÍNH symbol này, lấy
            // [VI] từ bảng tick theo-symbol của Quantower — nên đúng cho mọi công cụ
            // [VI] (ES, MES, NQ, cổ phiếu, FX, …) mà không hard-code giá trị nào.
            double dollarRiskPerContract = 0.0;

            try
            {
                if (CurrentSymbol != null)
                {
                    // A reference price is needed to resolve the active tick band.
                    // [VI] Cần một mức giá tham chiếu để xác định dải tick đang áp dụng.
                    double price = CurrentSymbol.Last;
                    if (price <= 0) price = CurrentSymbol.Bid;
                    if (price <= 0) price = CurrentSymbol.Ask;

                    var vt = CurrentSymbol.FindVariableTick(price);
                    double vtTickSize = vt.TickSize;   // price increment for this band  // [VI] bước giá của dải này
                    double vtTickCost = vt.TickCost;   // $ value of one tick / contract  // [VI] giá trị $ của 1 tick / contract

                    if (vtTickSize > 0 && vtTickCost > 0)
                        dollarRiskPerContract = (slDist / vtTickSize) * vtTickCost;
                }
            }
            catch { dollarRiskPerContract = 0.0; }

            // Fallback if the symbol's tick metadata is unavailable.
            // [VI] Phương án dự phòng nếu không có dữ liệu tick của symbol.
            if (dollarRiskPerContract <= 0)
            {
                double tickSize = CurrentSymbol?.TickSize ?? 0.25;
                if (tickSize <= 0) tickSize = 0.25;
                dollarRiskPerContract = (slDist / tickSize) * 0.5; // assume $0.50 / tick  // [VI] giả định $0.50 / tick
            }

            // Round DOWN so realised risk never exceeds the configured amount.
            // [VI] Làm tròn XUỐNG để rủi ro thực tế không bao giờ vượt mức đã cài.
            int contracts = (int)Math.Floor(RiskAmountUSD / dollarRiskPerContract);
            return Math.Max(1, contracts);                   // always trade at least 1  // [VI] luôn giao dịch tối thiểu 1
        }

        // --- Mode 2: Fixed contracts ---
        // [VI] --- Chế độ 2: Số contract cố định ---
        return NumContracts;
    }

    // =========================================================================
    //  RoundToTick
    //  Snaps a price to the nearest valid tick increment for the symbol.
    //  Falls back to 0.25 if the symbol's TickSize is unavailable.
    // -------------------------------------------------------------------------
    //  [VI] RoundToTick (LÀM TRÒN VỀ TICK)
    //  [VI] Bắt một mức giá về bước tick hợp lệ gần nhất của symbol.
    //  [VI] Dùng 0.25 làm dự phòng nếu không có TickSize của symbol.
    // =========================================================================
    private double RoundToTick(double price)
    {
        double tick = CurrentSymbol?.TickSize ?? 0.25;
        return tick > 0 ? Math.Round(price / tick) * tick : price;
    }

    // =========================================================================
    //  IsWithinTradingHours
    //  Converts the bar's UTC time to the session's local time using
    //  SessionUTCOffset, then checks whether it falls within [start, end).
    //  Special cases:
    //   - If end is 00:00 the session has no end (runs from start indefinitely).
    //   - If start > end the session wraps midnight (e.g. 22:00 – 02:00).
    // -------------------------------------------------------------------------
    //  [VI] IsWithinTradingHours (TRONG GIỜ GIAO DỊCH?)
    //  [VI] Chuyển giờ UTC của nến sang giờ địa phương của phiên bằng
    //       SessionUTCOffset, rồi kiểm tra có nằm trong [bắt đầu, kết thúc) không.
    //  [VI] Các trường hợp đặc biệt:
    //   [VI] - Nếu kết thúc là 00:00, phiên không có giờ kết thúc (chạy từ lúc bắt đầu vô hạn).
    //   [VI] - Nếu bắt đầu > kết thúc, phiên vắt qua nửa đêm (vd 22:00 – 02:00).
    // =========================================================================
    private bool IsWithinTradingHours(DateTime barTime)
    {
        if (!UseTradingHours) return true;
        DateTime localTime = barTime.AddHours(SessionUTCOffset);
        var current = localTime.TimeOfDay;
        var start = new TimeSpan(SessionStartHour, SessionStartMinute, 0);
        var end = new TimeSpan(SessionEndHour, SessionEndMinute, 0);
        if (end == TimeSpan.Zero) return current >= start;               // No end time.                  // [VI] Không có giờ kết thúc.
        if (start <= end) return current >= start && current < end;      // Same-day session.             // [VI] Phiên trong cùng ngày.
        return current >= start || current < end;                        // Overnight session.            // [VI] Phiên qua đêm.
    }

    // =========================================================================
    //  PrintBanner
    //  Writes a decorative ASCII-art banner to the strategy log to make
    //  strategy start events easy to spot at a glance.
    // -------------------------------------------------------------------------
    //  [VI] PrintBanner (IN BANNER)
    //  [VI] Ghi một banner ASCII trang trí ra log chiến lược để dễ nhận ra
    //       các sự kiện khởi động chiến lược trong nháy mắt.
    // =========================================================================
    private void PrintBanner(string status)
    {
        Log("╔══════════════════════════════════════════════════╗", StrategyLoggingLevel.Info);
        Log("║       Oneshot 2026 V2.3 Tele - QUANTOWER         ║", StrategyLoggingLevel.Info);
        Log("╚══════════════════════════════════════════════════╝", StrategyLoggingLevel.Info);
    }

    // =========================================================================
    //  OnGetMetrics
    //  Called by Quantower to populate the live "Strategy Metrics" panel.
    //  Returns a list of key-value pairs shown in the strategy dashboard.
    // -------------------------------------------------------------------------
    //  [VI] OnGetMetrics
    //  [VI] Được Quantower gọi để điền bảng "Strategy Metrics" theo thời gian thực.
    //  [VI] Trả về danh sách cặp khóa-giá trị hiển thị trên bảng điều khiển chiến lược.
    // =========================================================================
    [Obsolete]
    protected override List<StrategyMetric> OnGetMetrics()
    {
        double secondsSinceBar = _lastBarTime == DateTime.MinValue ? -1 : (DateTime.UtcNow - _lastBarTime).TotalSeconds;

        // Build a human-readable label for the active sizing mode.
        // [VI] Tạo nhãn dễ đọc cho chế độ tính size đang dùng.
        string sizingMode;
        if (UseRiskAmount)
            sizingMode = string.Format("Risk ${0:F0} / trade (auto-contracts)", RiskAmountUSD);
        else
            sizingMode = string.Format("Fixed {0} contracts", NumContracts);

        return new List<StrategyMetric>
        {
            new StrategyMetric { Name = "Bot Status",          FormattedValue = _inTrade ? "IN TRADE" : (_warmedUp ? "✅ RUNNING" : "⌛ LOADING HISTORY") },
            new StrategyMetric { Name = "Sizing Mode",         FormattedValue = sizingMode },
            new StrategyMetric { Name = "Trend",               FormattedValue = _warmedUp ? (_isUptrend ? "UPTREND ▲" : "DOWNTREND ▼") : "—" },
            new StrategyMetric { Name = "Last Signal",         FormattedValue = _lastSignal },
            new StrategyMetric { Name = "Position",            FormattedValue = _inTrade ? string.Format("{0} {1}x @ {2:F2}", _isLong ? "Long" : "Short", _fillQty, _entryPrice) : "Flat" },
            new StrategyMetric { Name = "SL / TP Status",      FormattedValue = _slTPSet ? string.Format("SL:{0:F2} | TP:{1:F2}", _slPrice, _tpPrice) : "—" },
            new StrategyMetric { Name = "Feed Health",         FormattedValue = secondsSinceBar < 0 ? "No bar received yet" : string.Format("Last bar {0:F1}s ago", secondsSinceBar) },
        };
    }

    // =========================================================================
    //  OnStop
    //  Called by Quantower when the user clicks "Stop". Performs a clean
    //  shutdown: stops all timers, unsubscribes events, cancels open orders,
    //  and disposes the historical data feed.
    // -------------------------------------------------------------------------
    //  [VI] OnStop
    //  [VI] Được Quantower gọi khi người dùng bấm "Stop". Thực hiện tắt sạch sẽ:
    //       dừng mọi timer, gỡ đăng ký sự kiện, hủy lệnh đang mở và giải phóng
    //       nguồn dữ liệu lịch sử.
    // =========================================================================
    protected override void OnStop()
    {
        // Telegram shutdown notification — sent first, before teardown begins.
        // [VI] Thông báo Telegram khi tắt — gửi trước khi bắt đầu dọn dẹp.
        SendTelegram("🔴 The Oneshot Trading System is offline.");

        // Stop the polling and watchdog timers.
        // [VI] Dừng các timer polling và watchdog.
        _mainPoller?.Dispose(); _mainPoller = null;
        _watchdogTimer?.Dispose(); _watchdogTimer = null;
        // Unsubscribe the trailing stop tick feed.
        // [VI] Gỡ đăng ký nguồn tick của trailing stop.
        UnsubscribeTrail();

        // Unsubscribe from platform position/order events.
        // [VI] Gỡ đăng ký các sự kiện vị thế/lệnh của nền tảng.
        Core.Instance.PositionAdded -= OnPositionAdded;
        Core.Instance.PositionRemoved -= OnPositionRemoved;
        Core.Instance.OrderRemoved -= OnOrderRemoved;
        Core.Instance.TradeAdded -= OnTradeAdded;

        // Cancel any outstanding SL/TP or orphan orders.
        // [VI] Hủy mọi lệnh SL/TP hoặc lệnh mồ côi còn tồn.
        CancelOrphanOrders();
        // Dispose the debounce timer if it is still pending.
        // [VI] Giải phóng timer debounce nếu còn đang chờ.
        lock (_lock) { _debounceTimer?.Dispose(); _debounceTimer = null; }
        // Dispose the historical data feed.
        // [VI] Giải phóng nguồn dữ liệu lịch sử.
        if (_hdm != null) { try { _hdm.Dispose(); } catch { } _hdm = null; }
        _ema = null;
    }
}
