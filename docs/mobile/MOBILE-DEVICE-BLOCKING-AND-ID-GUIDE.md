# دليل تكامل مطور الموبايل: حظر الأجهزة ومعرف الجهاز (Device ID & Device Blocking Guide)

> **الجمهور المستهدف**: مطورو تطبيق الموبايل (Flutter / Mobile Engineers).  
> **الحالة في الباك إند**: مكتمل ومفعل وجاهز للإنتاج (`main`).  
> **الهدف**: توضيح كيفية توليد وإرسال معرف الجهاز (`X-Device-Id`)، وكيفية التعامل مع حظر الأجهزة الصارم (`Device Blocking / Hard Ban`)، وأحداث العميل (`Events Track`).

---

## 1. ما هو نظام حظر الأجهزة ولماذا نحتاجه؟ (Why Device Blocking?)

عندما يقوم المشرف (Admin) بحظر مستخدم مسيء حظراً صارماً (**Hard Ban**):
1. يتم حظر حسابه وإيميله ومنعه من الدخول.
2. **المشكلة:** يستطيع المستخدم المسيء عمل حساب جديد بإيميل آخر في ثوانٍ والدخول من نفس الهاتف (تسمى هذه الظاهرة **Ban Evasion**).
3. **الحل:** يقوم الباك إند تلقائياً بحظر **الجهاز الفيزيائي** الذي استخدمه هذا الحساب.
4. لكي يتمكن السيرفر من التعرف على جهاز المستخدم وحظره، **يجب على تطبيق الموبايل إرسال معرف ثابت للجهاز في الـ Headers**.

> [!IMPORTANT]
> إذا لم يرسل تطبيق الموبايل هيدر `X-Device-Id`، فلن يتمكن السيرفر من حظر الجهاز عند معاقبة المستخدم المسيء، وسيتمكن من الرجوع للمنصة بحساب جديد فوراً!

---

## 2. مواصفات الـ HTTP Headers المطلوبة

يجب إرسال الـ Headers التالية عند كل طلب **تسجيل دخول (`Login`)** وتجديد الجلسة **(`RefreshToken`)**، والأفضل والأسهل إرسالها مع **كافة طلبات التطبيق** عبر الـ Interceptor:

| اسم الـ Header | إلزامي؟ | مثال للقيمة | الوصف |
|---|:---:|---|---|
| **`X-Device-Id`** | **نعم (إلزامي)** | `a3f1c9e2-4b21-4f11-b428-98e6c71021cb` | معرف فريد **وثابت** للجهاز لا يتغير بين تشغيل وآخر. |
| `X-Device-Name` | اختياري | `Ahmed's Galaxy` | الاسم التعريفي للهاتف إن وجد. |
| `X-Device-Model` | اختياري | `SM-S928B` أو `iPhone 15 Pro` | موديل الهاتف. |
| `X-Device-Type` | اختياري | `Android` أو `iOS` | منصة التشغيل. |
| `X-Device-Os` | اختياري | `14` أو `17.4` | إصدار نظام التشغيل. |

### شروط وقواعد `X-Device-Id`:
- **الثبات (Stability):** يجب ألا يتغير المعرف مع كل تشغيل للتطبيق (لا تولد UUID جديد في كل تشغيل). يجب توليده وحفظه في مساحة تخزين آمنة (`FlutterSecureStorage`) أو استخدام معرف النظام الثابت.
- **التطابق:** يجب إرسال نفس المعرف تماماً في `Login` و `RefreshToken`.
- **الحد الأقصى:** طول الـ ID لا يتجاوز 200 حرف (الباك إند سيقتطع أي نص أطول من ذلك تلقائياً).

---

## 3. التعامل مع استجابة الحظر (Handling 403 Device Blocked)

عندما يكون الجهاز محظوراً، يقوم الـ Middleware في الباك إند (`DeviceBlockingMiddleware`) باعتراض أي طلب قادم من هذا الجهاز فوراً ويرجع كود **`403 Forbidden`** بالشكل التالي:

```http
HTTP/1.1 403 Forbidden
Content-Type: application/json

{
  "statusCode": 403,
  "message": "This device has been permanently blocked from accessing the system."
}
```

### ماذا يجب أن يفعل تطبيق الموبايل عند استلام هذا الخطأ؟
1. التقاط الخطأ في الـ `Dio / HTTP Error Interceptor`.
2. مسح بيانات الجلسة المخزنة محلياً (التوكنز وبيانات المستخدم).
3. قطع أي اتصال مباشر بالـ SignalR (`RoomHub`).
4. توجيه المستخدم فوراً إلى شاشة خاصة: **"تم حظر هذا الجهاز نهائياً من الوصول إلى المنصة لمخالفة شروط الاستخدام"** مع منع أي محاولة لتسجيل الدخول مجدداً من نفس الجهاز.

---

## 4. كود فلاتر كامل وجاهز (Flutter Implementation)

### أ) إضافة الاعتماديات (Dependencies) في `pubspec.yaml`:
```yaml
dependencies:
  dio: ^5.4.0
  device_info_plus: ^10.1.0
  flutter_secure_storage: ^9.0.0
  uuid: ^4.3.3
```

---

### ب) خدمة إدارة بيانات الجهاز (`DeviceService`):
```dart
import 'dart:io';
import 'package:device_info_plus/device_info_plus.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:uuid/uuid.dart';

class DeviceInfoData {
  final String deviceId;
  final String deviceName;
  final String deviceModel;
  final String deviceType;
  final String deviceOs;

  DeviceInfoData({
    required this.deviceId,
    required this.deviceName,
    required this.deviceModel,
    required this.deviceType,
    required this.deviceOs,
  });

  Map<String, String> toHeaders() {
    return {
      'X-Device-Id': deviceId,
      'X-Device-Name': deviceName,
      'X-Device-Model': deviceModel,
      'X-Device-Type': deviceType,
      'X-Device-Os': deviceOs,
    };
  }
}

class DeviceService {
  static const _storage = FlutterSecureStorage();
  static const _keyDeviceId = 'cocorra_persistent_device_id';
  static DeviceInfoData? _cachedInfo;

  /// الحصول على تفاصيل الجهاز والـ Device ID الثابت
  static Future<DeviceInfoData> getDeviceInfo() async {
    if (_cachedInfo != null) return _cachedInfo!;

    final deviceInfoPlugin = DeviceInfoPlugin();
    String deviceId = '';
    String deviceName = '';
    String deviceModel = '';
    String deviceType = Platform.isAndroid ? 'Android' : (Platform.isIOS ? 'iOS' : 'Other');
    String deviceOs = '';

    if (Platform.isAndroid) {
      final androidInfo = await deviceInfoPlugin.androidInfo;
      deviceName = androidInfo.brand;
      deviceModel = androidInfo.model;
      deviceOs = androidInfo.version.release;

      // Android: استخدام UUID يتم توليده وحفظه في SecureStorage لضمان ثباته
      String? savedId = await _storage.read(key: _keyDeviceId);
      if (savedId == null || savedId.isEmpty) {
        // إذا لم يكن موجوداً، نولده مرة واحدة ونخزنه
        savedId = const Uuid().v4();
        await _storage.write(key: _keyDeviceId, value: savedId);
      }
      deviceId = savedId;
    } else if (Platform.isIOS) {
      final iosInfo = await deviceInfoPlugin.iosInfo;
      deviceName = iosInfo.name;
      deviceModel = iosInfo.utsname.machine;
      deviceOs = iosInfo.systemVersion;

      // iOS: identifierForVendor ثابت لكل تطبيق من نفس المطور
      deviceId = iosInfo.identifierForVendor ?? const Uuid().v4();
    }

    _cachedInfo = DeviceInfoData(
      deviceId: deviceId,
      deviceName: deviceName,
      deviceModel: deviceModel,
      deviceType: deviceType,
      deviceOs: deviceOs,
    );

    return _cachedInfo!;
  }
}
```

---

### جـ) الـ Interceptor في `Dio` لإرسال الهيدرز واعتراض الحظر تلقائياً:
```dart
import 'package:dio/dio.dart';
import 'device_service.dart';

class DeviceAndAuthInterceptor extends Interceptor {
  final Function() onDeviceBlocked; // Callback للانتقال لشاشة الحظر

  DeviceAndAuthInterceptor({required this.onDeviceBlocked});

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) async {
    // جلب الهيدرز الخاصة بالجهاز وإرفاقها مع كل ريكويست
    final deviceInfo = await DeviceService.getDeviceInfo();
    options.headers.addAll(deviceInfo.toHeaders());

    return handler.next(options);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) {
    // التقاط خطأ حظر الجهاز (403 Device Blocked)
    if (err.response?.statusCode == 403) {
      final responseData = err.response?.data;
      if (responseData is Map &&
          responseData['message']?.toString().contains('permanently blocked') == true) {
        // استدعاء دالة طرد المستخدم وشاشة الحظر
        onDeviceBlocked();
      }
    }
    return handler.next(err);
  }
}
```

---

## 5. ملخص أحداث التتبع الخاصة بالعميل (`POST /api/events/track`)

للتذكير، يحتوي النظام أيضاً على Endpoint مخصصة لتتبع الأحداث التي لا يراها السيرفر:

```http
POST /api/events/track
Authorization: Bearer <JWT>
Content-Type: application/json

{
  "eventType": "feature_viewed",
  "properties": { "screen": "profile", "tab": "friends" }
}
```

- **القائمة البيضاء المسموحة فقط (Allowlist):**
  1. `room_create_started`: عند فتح نافذة إنشاء غرفة.
  2. `notification_opened`: عند الضغط على إشعار Push Notification ودخول التطبيق.
  3. `feature_viewed`: عند فتح ميزة محددة لقياس التفاعل.
- **القواعد:**
  - الإرسال يتم بنظام **Fire-and-Forget** (لا توقف التطبيق ولا تظهر أخطاء للمستخدم أبداً في حال فشل الإرسال).
  - لا ترسل أي بيانات شخصية (PII) داخل `properties` (لا ترسل إيميل، أرقام هواتف، أو نصوص مستخدم).

---

## 6. قائمة التحقق لمطور الموبايل (Checklist)

- [ ] التأكد من تجهيز `DeviceService` وتوليد `X-Device-Id` ثابت لا يتغير بين تشغيل وآخر.
- [ ] إضافة `X-Device-Id` في الـ Headers لكافة الريكويستات (أو على الأقل في `Login` و `RefreshToken`).
- [ ] فحص كود الاستجابة `403` والتأكد من توجيه المستخدم لشاشة "الجهاز محظور" عند الحظر الصارم.
- [ ] التأكد من أن أحداث `POST /api/events/track` لا تُرسل سوى الأحداث الثلاثة المسموحة فقط وبشكل غير متزامن (Fire-and-Forget).
