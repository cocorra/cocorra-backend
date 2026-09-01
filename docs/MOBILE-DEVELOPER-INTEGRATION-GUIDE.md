# دليل تكامل مطور الموبايل (Flutter Developer Integration Guide)
## نظام التتبع والتحليلات في Cocorra Backend

> **الجمهور المستهدف**: مطورو تطبيق الموبايل (Flutter / Mobile Team).  
> **الهدف**: توضيح دور تطبيق الموبايل في منظومة التتبع، وتحديد الـ Endpoints والأحداث المسموح بإرسالها، وتأكيد المعايير البرمجية لضمان عمل التطبيق بسلاسة تامة.

---

## 1. مبدأ أساسي: دور تطبيق الموبايل (Important Context)

> [!IMPORTANT]
> - تطبيق الموبايل هو **تطبيق المستخدم النهائي (End-User Client)** وليس لوحة تحكم إدارية.
> - التطبيق **لن يعرض أي رسوم بيانية أو تحليلات (Analytics Dashboards)** للمستخدم؛ فالتحليلات مخصصة للوحة الإدارة فقط (`admin.cocorraapp.com`).
> - **أكثر من 90% من عمليات التتبع تتم تلقائياً من الباك إند (Server-Authoritative)** عند استدعاء الـ APIs أو أحداث SignalR، ولن تحتاج لكتابة كود تتبع يدوي لأغلب العمليات (مثل الانضمام للغرف، رفع اليد، التحدث، التسجيل، التحقق الصوتي).

---

## 2. الأحداث المسموح للتطبيق بإرسالها (Client-Allowed Events)

لحماية دقة البيانات ومنع التلاعب بمسارات الـ Funnel، يطبق الباك إند **قائمة بيضاء صارمة (Allowlist)**. لا يُسمح للتطبيق بإرسال إلا **3 أحداث فقط** عبر الـ Endpoint المخصصة:

### الـ Endpoint:
```http
POST /api/events/track
Authorization: Bearer <USER_JWT_TOKEN>
Content-Type: application/json
```

### هيكل الطلب (Request Body DTO):
```json
{
  "eventType": "string",
  "properties": { }
}
```

---

### جدول الأحداث المسموحة:

| الحدث (`eventType`) | متى يتم إرساله من الموبايل؟ | مثال للـ `properties` |
|---|---|---|
| `room_create_started` | لحظة فتح المستخدم لشاشة أو نافذة "إنشاء غرفة" (قبل الضغط على زر الإنشاء الفعلي). | `{ "source": "home_fab" }` أو `{}` |
| `notification_opened` | عند ضغط المستخدم على إشعار Push Notification والدخول للتطبيق من خلاله. | `{ "notificationType": "RoomReminder", "referenceId": "GUID" }` |
| `feature_viewed` | عند فتح المستخدم لميزة جديدة أو تبويب رئيسي معين ترغب الإدارة في قياس مدى الوصول له. | `{ "featureName": "TopicVoting" }` |

> [!WARNING]
> إذا حاول التطبيق إرسال أي اسم حدث آخر غير هذه الثلاثة (مثل `room_created` أو `activation_completed`)، سيرفض السيرفر الطلب بـ `400 Bad Request` مع رسالة:  
> `"EventType is not permitted from clients."`  
> وذلك لأن تلك الأحداث يتم تسجيلها في السيرفر بعد نجاح العملية في قاعدة البيانات مباشرة.

---

## 3. التعامل مع الغرف الصوتية و SignalR (`RoomHub`)

جميع تحليلات الغرف الصوتية يتم حسابها تلقائياً على السيرفر، والمطلوب فقط من تطبيق الموبايل هو الالتزام بالاستدعاء الصحيح للـ Hub Methods:

1. **الانضمام والمغادرة (`JoinRoom` / `LeaveRoom`):**
   - استدعِ `JoinRoom(roomId)` عند دخول شاشة الغرفة.
   - استدعِ `LeaveRoom(roomId)` عند الخروج الطبيعي، وإذا حدث انقطاع اتصال مفاجئ (Network Drop) فإن السيرفر سيتولى إغلاق الجلسة تلقائياً في `OnDisconnectedAsync`.
2. **كتم وإلغاء كتم المايكروفون (`MuteAudio` / `UnmuteAudio`):**
   - عندما يفتح المستخدم المايك: استدعِ `UnmuteAudio(roomId)`.
   - عندما يكتم المستخدم المايك: استدعِ `MuteAudio(roomId)`.
   - *السيرفر يقوم بحساب أجزاء الثواني التي تحدث فيها المستخدم تلقائياً بناءً على هذه الأحداث.*
3. **طلب الصعود للمسرح (`RaiseHand` / `LowerHand`):**
   - لطلب الصعود للمسرح: استدعِ `RaiseHand(roomId)`.
   - لسحب الطلب: استدعِ `LowerHand(roomId)`.

---

## 4. التوافق مع استجابات الـ API (`Response<T>.Meta`)

يحتوي كل رد قياسي من الباك إند على حقل عام باسم `meta`:
```json
{
  "succeeded": true,
  "message": null,
  "data": { ... },
  "meta": null
}
```

> [!TIP]
> - في كود Flutter / Dart، تأكد من تعريف `meta` كـ `dynamic` أو حقل اختياري (`Map<String, dynamic>? meta;`) في الـ `BaseResponse` Model.
> - حقل `meta` يستخدم لنقل بيانات إضافية للداشبورد أو التتبع المستقبلي، وتجاهله من جانب تطبيق الموبايل لن يؤثر إطلاقاً على سير العمل.

---

## 5. أمثلة كود Flutter (Dart / Dio Snippets)

### مثال 1: خدمة إرسال أحداث العميل (Analytics Tracking Service)

```dart
import 'package:dio/dio.dart';

class AnalyticsTracker {
  final Dio _dio;

  AnalyticsTracker(this._dio);

  /// 1. إرسال حدث فتح شاشة إنشاء غرفة
  Future<void> trackRoomCreateStarted({String? source}) async {
    await _sendEvent(
      eventType: 'room_create_started',
      properties: {
        if (source != null) 'source': source,
      },
    );
  }

  /// 2. إرسال حدث فتح الإشعار
  Future<void> trackNotificationOpened({
    required String notificationType,
    String? referenceId,
  }) async {
    await _sendEvent(
      eventType: 'notification_opened',
      properties: {
        'notificationType': notificationType,
        if (referenceId != null) 'referenceId': referenceId,
      },
    );
  }

  /// 3. إرسال حدث عرض ميزة معينة
  Future<void> trackFeatureViewed(String featureName) async {
    await _sendEvent(
      eventType: 'feature_viewed',
      properties: {
        'featureName': featureName,
      },
    );
  }

  /// دالة الإرسال العامة (Fire-and-Forget بدون تعطيل تجربة المستخدم)
  Future<void> _sendEvent({
    required String eventType,
    Map<String, dynamic>? properties,
  }) async {
    try {
      await _dio.post(
        '/api/events/track',
        data: {
          'eventType': eventType,
          'properties': properties ?? {},
        },
      );
    } catch (e) {
      // التتبع يجب ألا يوقف التطبيق أبداً أو يظهر رسائل خطأ للمستخدم
      print('Analytics tracking failed silently: $e');
    }
  }
}
```

### مثال 2: استدعاء التتبع عند فتح شاشة إنشاء الغرفة
```dart
void onOpenCreateRoomBottomSheet(BuildContext context) {
  // تتبع فتح الشاشة في الخلفية
  analyticsTracker.trackRoomCreateStarted(source: 'home_floating_button');

  // فتح الـ BottomSheet
  showModalBottomSheet(
    context: context,
    builder: (context) => const CreateRoomSheet(),
  );
}
```

---

## 6. ملخص المهام المطلوبة من مطور الموبايل (Checklist)

- [ ] التأكد من وجود كلاس/دالة إرسال الـ Events (`POST /api/events/track`).
- [ ] استدعاء `room_create_started` عند فتح نافذة إنشاء الغرفة.
- [ ] استدعاء `notification_opened` عند النقر على إشعار من مركز الإشعارات.
- [ ] التأكد من أن الـ SignalR Hub يستدعي `MuteAudio` و `UnmuteAudio` و `RaiseHand` و `LowerHand` بشكل صحيح.
- [ ] التأكد من أن `BaseResponse` في Dart يقبل `meta` كقيمة اختيارية (`dynamic` / `nullable`).
- [ ] **لا يوجد أي عمل مطلوب بخصوص شاشات الداشبورد أو الرسوم البيانية.**
