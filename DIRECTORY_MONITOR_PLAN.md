# Directory Monitor — Kế hoạch phát triển

## Mục tiêu

Directory Monitor là ứng dụng WPF chạy trên Windows, dùng để theo dõi đồng thời nhiều thư mục local hoặc UNC và ghi nhận thay đổi của file/folder theo thời gian thực.

Bốn loại sự kiện chuẩn:

| ID | Sự kiện | Ý nghĩa |
|---:|---|---|
| 1 | New | Tạo file hoặc folder mới |
| 2 | Change | Sửa nội dung, rename hoặc move |
| 3 | Modify Properties | Thay đổi metadata/attributes |
| 4 | Delete | Xoá file hoặc folder |

Ứng dụng ghi đồng thời tới một hoặc nhiều output: text file, PostgreSQL, SQL Server và Windows Application Event Log.

## Công nghệ và nền tảng

- .NET 9, C#, WPF native và MVVM.
- CommunityToolkit.Mvvm; không dùng UI framework thương mại.
- SQLite cho cấu hình, snapshot và durable outbox.
- Npgsql cho PostgreSQL; Microsoft.Data.SqlClient cho SQL Server.
- Windows 10/11 x64 và Windows Server 2022/2025 Desktop Experience.
- Bản phát hành self-contained `win-x64`.
- Solution sử dụng định dạng `DirectoryMonitor.slnx`; không duy trì file `.sln` cũ.

## Quản lý directory

- Thêm, sửa, enable/disable và xoá mềm nhiều directory.
- Theo dõi file, folder hoặc cả hai; có thể theo dõi toàn bộ thư mục con.
- Cấu hình include/exclude glob patterns.
- Mỗi directory chọn trực tiếp một hoặc nhiều output destination.
- UNC sử dụng quyền của Windows account đang chạy ứng dụng.
- Lần đầu thêm directory chỉ tạo baseline, không phát event cho dữ liệu đã có.
- Snapshot reconciliation chạy khi startup, resume, UNC reconnect, watcher overflow và hằng ngày.
- Thay đổi `LastWriteTime` tự động của directory cha do tạo/sửa/xoá file con không phát sinh `Change` riêng cho directory; thay đổi attributes thực sự của directory vẫn được phân loại `Modify Properties`.
- Directory grid không dùng cột checkbox Disabled. Cột Status hiển thị trạng thái runtime `Monitoring`, `Disabled`, `Unavailable`, `Reconnecting` hoặc `Error`; trạng thái tạm thời này không ghi vào SQLite.

Dialog Add/Edit Directory dựa trên thiết kế khách hàng cung cấp, gồm hai tab:

1. **Main:** Path, Description, event checkboxes, subdirectories, entry type và filters.
2. **Outputs:** Danh sách destination dạng checkbox.

Không triển khai Execute, Sounds, Emailer, Growl, Print, Syslog, File Access hoặc inactivity event.

## Giao diện chính

Màn hình chính hiển thị DataGrid realtime với các cột:

- Event time `dd/MM/yyyy HH:mm:ss.fff`.
- Event type và entry type.
- Path và OldPath.
- File size, Windows user, machine name và delivery status.

Event grid khóa thứ tự thời gian từ cũ đến mới, event mới nhất luôn nằm dưới cùng và tự động cuộn xuống sau mỗi batch mới. Khi vượt giới hạn buffer, các event cũ nhất ở đầu danh sách được loại bỏ.

Toàn bộ cell và header của Directory, Event và Output DataGrid được căn giữa theo chiều dọc. Riêng cột Status được căn giữa theo cả hai chiều và hiển thị dạng badge màu. Output grid dùng các trạng thái `Ready`, `Delivering`, `Retrying`, `Disabled`; thao tác bật/tắt thực hiện bằng nút riêng.

Toàn bộ `ListBoxItem` cũng căn giữa nội dung theo chiều dọc và kéo giãn nội dung theo chiều ngang. Thanh tiêu đề có nút `Exit/Thoát` riêng: người dùng phải xác nhận trước khi app dừng watcher/output worker; nút `X` vẫn chỉ thu ứng dụng xuống system tray.

Path/OldPath là hyperlink:

- Folder mở bằng Explorer.
- File mở bằng ứng dụng mặc định.
- Executable/script yêu cầu xác nhận.
- Path đã xoá mở thư mục cha gần nhất.
- Context menu hỗ trợ Open, Open containing folder, Copy path và Copy full event.

Màu sự kiện:

| Event | Accent | Light background | Light text | Dark background | Dark text |
|---|---|---|---|---|---|
| New | `#22C55E` | `#DCFCE7` | `#166534` | `#052E16` | `#86EFAC` |
| Change | `#3B82F6` | `#DBEAFE` | `#1D4ED8` | `#172554` | `#93C5FD` |
| Modify Properties | `#F59E0B` | `#FEF3C7` | `#92400E` | `#451A03` | `#FCD34D` |
| Delete | `#EF4444` | `#FEE2E2` | `#B91C1C` | `#450A0A` | `#FCA5A5` |

## Output và độ tin cậy

Mọi event được commit vào SQLite cùng delivery records trước khi gửi. SQLite mặc định nằm tại `<thư mục chứa Directory Monitor.exe>\Data\directory-monitor.db`; biến `DIRECTORY_MONITOR_DATA` vẫn có thể override khi phát triển/test. Khi nâng cấp, nếu DB mới chưa tồn tại nhưng DB cũ trong `%ProgramData%\Directory Monitor` có tồn tại, ứng dụng dùng SQLite backup để chuyển toàn bộ dữ liệu và giữ nguyên DB cũ làm dự phòng. Mỗi destination có worker và retry riêng; một output lỗi không chặn output khác. Credential database được mã hoá bằng DPAPI CurrentUser.

Settings hiển thị đường dẫn SQLite tuyệt đối đã resolve và có nút mở thư mục `Data`. File database chỉ được tạo trong lần khởi động thành công đầu tiên; SQLite có thể tạo thêm các file `-wal` và `-shm` khi ứng dụng đang chạy.

### Text output

Người dùng định nghĩa file path, format và nội dung bằng token:

`{EventType}`, `{EventTime}`, `{EventTime:format}`, `{EventTimeUtc}`, `{EntryType}`, `{Path}`, `{OldPath}`, `{FileSize}`, `{UserName}`, `{MachineName}`, `{MachineId}`, `{EventId}`, `{SequenceNumber}`, `{IsRecovered}`.

Hỗ trợ Plain Text, JSON Lines, CSV và Custom Template; UTF-8; preview/test write; rotation hằng ngày hoặc 100 MB và retention 30 ngày.

### PostgreSQL và SQL Server

Người dùng chỉ nhập connection string. Khi Test & Save, ứng dụng tự:

1. Kiểm tra kết nối và lấy migration lock.
2. Tạo schema/tables/index/foreign keys nếu thiếu.
3. Seed EventTypes.
4. Chạy migration và ghi `__DirectoryMonitorSchemaHistory`.
5. Chỉ gửi outbox khi schema ở trạng thái Ready.

Các bảng nghiệp vụ:

- `Directories`: Id, MachineId, Path, IsDisabled, IsDeleted, DeletedTime, CreationTime, LastModification.
- `EventTypes`: Id, Name, CreationTime.
- `EventInfos`: Id, SequenceNumber, EventTime, EventTypeId, DirectoryId, EntryType, Path, OldPath, FileSize, UserName, MachineId, MachineName, IsRecovered, CreationTime.

PostgreSQL dùng schema mặc định `public`; SQL Server dùng schema mặc định `dbo`.

Ghi database có tính idempotent theo cả UUID event và cặp `(MachineId, SequenceNumber)`. Event đã tồn tại được xem là đã gửi thành công thay vì bị retry vô hạn. Khi một output lỗi, người dùng chọn dòng tương ứng để xem toàn bộ lỗi nhiều dòng trong khung chi tiết chỉ đọc và có thể sao chép lỗi.

### Windows Event Log

Ghi vào Windows Logs > Application với source `Directory Monitor`. Event IDs 1001–1004 tương ứng bốn event; 9001+ dành cho lỗi vận hành.

## Hiệu năng

Pipeline chạy nền:

```text
FileSystemWatcher → bounded channel → debounce/classification
                  → SQLite outbox → UI batches + output workers
```

- Không I/O trên watcher callback hoặc UI thread.
- UI cập nhật theo batch mỗi 100–200 ms, tối đa 100 rows/lần.
- DataGrid virtualization và buffer mặc định 10.000 rows.
- SQLite WAL, prepared/batched transactions.
- Database delivery tối đa 100 events/transaction.
- Text template được validate/compile theo destination và writer dùng buffer 64 KB.
- Mục tiêu: 500+ event/giờ và burst 100 event/giây; UI không freeze và không mất event đã commit vào outbox.

## Vận hành và phát hành

- Đóng cửa sổ sẽ thu xuống system tray; Exit mới dừng monitoring.
- Auto-start bằng HKCU Run khi account vận hành đăng nhập.
- Global mutex bảo đảm một instance trên máy.
- Tiêu đề thêm `(Administrator)` khi process elevated.
- Installer per-machine đăng ký Windows Event Log source, tạo `{app}\Data` và chỉ cấp quyền users-modify cho thư mục dữ liệu này.
- Custom icon folder xanh kết hợp activity pulse dùng cho executable, tray và installer.

## Cập nhật ứng dụng

- Phiên bản hiện tại `1.1.8`; version được quản lý tập trung cho project, installer và package.

## Bổ sung 1.1.8

- SQLite lấy connection string từ `appsettings.json`; thay đổi khi chạy chỉ báo cần restart và không đổi durable outbox live.
- Serilog ghi rolling file hằng ngày trong `Logs`, reload cấu hình live và bắt các lỗi vòng đời/global exception.
- Theme gồm System, Light và Dark với palette động cho toàn bộ control.
- Directory/Output grid hỗ trợ context menu, duplicate Disabled và double-click để sửa; Event grid có Clear UI-only và copy TSV.
- Import/Export hỗ trợ JSON, XML và `.dm` mã hoá; imported event không tạo delivery hoặc gửi lại output.
- Manifest URL do người dùng cấu hình, mặc định trống và chỉ chấp nhận HTTPS không xác thực.
- Lịch kiểm tra: Every startup, Daily, Monthly hoặc Never; Check now luôn khả dụng.
- Khi có bản mới, badge trên thanh tiêu đề mở dialog release notes song ngữ với Update now, Skip this version và Later.
- Installer tải theo stream, giới hạn 1 GB, bắt buộc đúng size và SHA-256 trong manifest; file chưa xác minh không được chạy.
- Trước khi yêu cầu UAC, app tạo SQLite backup nhất quán. Installer không ghi đè DB tại Program Files và hỗ trợ import DB khi chuyển từ portable lần đầu.
- Chưa enforce Authenticode; tài liệu phát hành cảnh báo Windows SmartScreen có thể xuất hiện.

## Kiểm thử và bàn giao

- Unit/integration tests cho filters, template, SQLite outbox, watcher, retry và database migrations.
- Acceptance tests trên Windows 10/11 và Windows Server 2022 Desktop Experience.
- Bàn giao source code, installer, schema documentation, vận hành, test report và checklist nghiệm thu.
