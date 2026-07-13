import http from 'k6/http';
import ws from 'k6/ws';
import { sleep, check } from 'k6';

export const options = {
  stages: [
    { duration: '1m', target: 50 },   // رفع عدد المستخدمين لـ 50 خلال دقيقة
    { duration: '3m', target: 50 },   // الثبات على 50 مستخدم لمدة 3 دقايق
    { duration: '1m', target: 100 },  // رفع الضغط لـ 100 مستخدم (Peak Load) في دقيقة
    { duration: '2m', target: 100 },  // الثبات على 100 مستخدم لمدة دقيقتين
    { duration: '1m', target: 0 },    // تقليل العدد لـ 0 بالتدريج (نهاية الاختبار)
  ],
};

const TOKEN = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxM2FlNzMxZi01MDE5LTQyNzktZTAxNS0wOGRlYmY4NjdkMDYiLCJlbWFpbCI6ImNvY29ycmEwMkBnbWFpbC5jb20iLCJqdGkiOiJmZDQwMjkwZS1hYTEyLTRiZjgtODRiMy1mMTNjZjA2MDA5OWQiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1laWRlbnRpZmllciI6IjEzYWU3MzFmLTUwMTktNDI3OS1lMDE1LTA4ZGViZjg2N2QwNiIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJjb2NvcnJhMDJAZ21haWwuY29tIiwicHJvZmlsZVBpY3R1cmUiOiIiLCJWZXJpZmljYXRpb25TdGF0dXMiOiJBY3RpdmUiLCJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dzLzIwMDgvMDYvaWRlbnRpdHkvY2xhaW1zL3JvbGUiOlsiQWRtaW4iLCJDb2FjaCJdLCJleHAiOjE3ODEyNTIzMDMsImlzcyI6Imh0dHBzOi8vYXBpLmNvY29ycmFhcHAuY29tIiwiYXVkIjoiQ29jb3JyYU1vYmlsZUFwcCJ9.SHd4afjm8MeDUfEtjVxZEGD0SmdgksKNUrP3Jfe676U';

const BASE_URL = 'https://api.cocorraapp.com/api/v1';
const WS_BASE_URL = 'wss://api.cocorraapp.com/hubs';

export default function () {
  const params = {
    headers: {
      'Authorization': `Bearer ${TOKEN}`,
      'Content-Type': 'application/json',
    },
  };

  // 1. اختبار أهم نقاط النهاية (Endpoints) الخاصة بالأدمن والمستخدم
  const endpoints = [
    'https://api.cocorraapp.com/api/v1/Admin/Users',
    'https://api.cocorraapp.com/api/v1/Admin/Dashboard/Stats',
    'https://api.cocorraapp.com/api/v1/Roles/List',
    'https://api.cocorraapp.com/api/v1/Room/Feed',
    'https://api.cocorraapp.com/api/v1/Room/admin/history',
    'https://api.cocorraapp.com/api/v1/Support/admin/reports',
    'https://api.cocorraapp.com/api/v1/Support/chat/my-chat',
    'https://api.cocorraapp.com/api/Profile/me',
    'https://api.cocorraapp.com/api/Notifications/my-notifications',
    'https://api.cocorraapp.com/api/Chat/friends-list'
  ];

  // دمج كل الطلبات علشان تتبعت كـ Batch
  const requests = endpoints.map(url => ({
    method: 'GET',
    url: url,
    params: params
  }));

  const responses = http.batch(requests);

  responses.forEach((res, index) => {
    const is200 = check(res, {
      [`${endpoints[index]} is 200`]: (r) => r.status === 200,
    });
    if (!is200) {
      console.log(`[Error] Endpoint: ${endpoints[index]} | Status: ${res.status}`);
    }
  });

  // 2. اختبار الـ SignalR Hubs (WebSockets)
  // بنجرب نتصل بـ Room Hub ونعمل Handshake الخاص بـ SignalR
  const wsUrl = `${WS_BASE_URL}/rooms?access_token=${TOKEN}`;
  
  const resWs = ws.connect(wsUrl, params, function (socket) {
    socket.on('open', () => {
      // إرسال بروتوكول SignalR لبدء الاتصال بنجاح
      socket.send('{"protocol":"json","version":1}\x1e');
    });

    socket.on('error', (e) => {
      if (e.error() != "websocket: close sent") {
        console.log(`[WS Error] ${e.error()}`);
      }
    });

    // إغلاق الاتصال بعد 3 ثواني لتخفيف الحمل بعد إثبات نجاح الاتصال
    socket.setTimeout(function () {
      socket.close();
    }, 3000);
  });

  check(resWs, { 'Room Hub connected successfully': (r) => r && r.status === 101 });

  sleep(1);
}