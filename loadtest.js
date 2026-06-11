import http from 'k6/http';
import { sleep, check } from 'k6';

export const options = {
  stages: [
    { duration: '30s', target: 10 },  // رفع عدد المستخدمين لـ 10 خلال 30 ثانية
    { duration: '1m', target: 10 },   // الثبات على 10 مستخدمين لمدة دقيقة
    { duration: '30s', target: 0 },   // تقليل العدد لـ 0 (نهاية الاختبار)
  ],
};

export default function () {
  // ضع التوكن الخاص بك هنا
  const TOKEN = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxM2FlNzMxZi01MDE5LTQyNzktZTAxNS0wOGRlYmY4NjdkMDYiLCJlbWFpbCI6ImNvY29ycmEwMkBnbWFpbC5jb20iLCJqdGkiOiJmZDQwMjkwZS1hYTEyLTRiZjgtODRiMy1mMTNjZjA2MDA5OWQiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1laWRlbnRpZmllciI6IjEzYWU3MzFmLTUwMTktNDI3OS1lMDE1LTA4ZGViZjg2N2QwNiIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJjb2NvcnJhMDJAZ21haWwuY29tIiwicHJvZmlsZVBpY3R1cmUiOiIiLCJWZXJpZmljYXRpb25TdGF0dXMiOiJBY3RpdmUiLCJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dzLzIwMDgvMDYvaWRlbnRpdHkvY2xhaW1zL3JvbGUiOlsiQWRtaW4iLCJDb2FjaCJdLCJleHAiOjE3ODEyNTIzMDMsImlzcyI6Imh0dHBzOi8vYXBpLmNvY29ycmFhcHAuY29tIiwiYXVkIjoiQ29jb3JyYU1vYmlsZUFwcCJ9.SHd4afjm8MeDUfEtjVxZEGD0SmdgksKNUrP3Jfe676U';

  const params = {
    headers: {
      'Authorization': `Bearer ${TOKEN}`,
      'Content-Type': 'application/json',
    },
  };

  // المسار الصحيح للـ API
  const url = 'https://api.cocorraapp.com/api/v1/room/feed';

  const res = http.get(url, params);

  // التأكد إن الرد راجع سليم (Status 200)
  const is200 = check(res, {
    'is status 200': (r) => r.status === 200,
  });

  // طباعة الخطأ في حال لم يكن الرد 200
  if (!is200) {
    console.log(`[Error] Status: ${res.status} | Body: ${res.body}`);
  }

  sleep(1); // استراحة ثانية بين كل ريكوست والتاني لكل مستخدم
}