# إعداد GitHub App مرة واحدة

ميزة الدخول بالمتصفح مكتملة في البرنامج، لكنها تحتاج GitHub App مسجلًا من حسابك ومثبتًا على المستودع. لا يوجد Client ID جاهز داخل المصدر. الاتصال عبر GitHub داخل ChatGPT لا يُعتبر تسجيلًا لهذا التطبيق الجديد.

1. افتح https://github.com/settings/apps/new وسجّل الدخول بحساب مالك الريبو.
2. الاسم: اختر اسمًا فريدًا مثل `waleed-editexcel-desktop`، وHomepage URL: `https://github.com/waleedzidan2020/editexcel`.
3. فعّل **Enable Device Flow**. لا تحتاج Callback URL لتدفق Device Flow. لا تفعّل طلب OAuth أثناء التثبيت.
4. ألغِ **Active** تحت Webhook؛ هذا التطبيق المكتبي لا يستقبل Webhooks.
5. في Repository permissions اجعل **Contents: Read and write** فقط. Metadata للقراءة تُضاف تلقائيًا. اترك بقية الصلاحيات بدون وصول.
6. اختر **Only on this account** وأنشئ التطبيق.
7. من **Install App** ثبّته على حسابك، واختر **Only select repositories → editexcel**.
8. انسخ **Client ID** من صفحة General (ليس App ID، وليس Client Secret).
9. افتح البرنامج، وافتح قسم «تسجيل الدخول بالمتصفح — GitHub App»، والصق Client ID مرة واحدة ثم اضغط «تسجيل الدخول بـGitHub».
10. أدخل الكود الظاهر في نافذة البرنامج في صفحة GitHub التي فتحها المتصفح، ثم وافق. ارجع للبرنامج واضغط «اتصال وفتح Excel».

Client ID معرف عام وليس كلمة سر. لا تولّد أو تضع Client Secret أو Private Key في البرنامج أو الريبو؛ Device Flow لا يحتاجهما.

## تذكّر الدخول

عند اختيار «تذكّر الدخول»، تُخزَّن بيانات الوصول باستخدام Windows DPAPI بنطاق CurrentUser في `%LOCALAPPDATA%\EditExcel\credential.dpapi`. لا تُكتب كنص واضح. يستطيع حساب Windows نفسه فكّها؛ هذه ليست حماية من برنامج خبيث يعمل بصلاحيات نفس الحساب. Client ID يُحفظ كنص لأنه معرف عام.

زر «نسيان الدخول المحفوظ» يحذف النسخة المحلية فقط. إلغاء التفويض من GitHub يتم من Settings → Applications، وإلغاء تثبيت التطبيق من Installed GitHub Apps.

تذاكر GitHub App تنتهي افتراضيًا بعد 8 ساعات. هذه النسخة تطلب تسجيل الدخول من المتصفح مجددًا عند الانتهاء أو عند إلغاء الوصول؛ لا تجدد التذاكر تلقائيًا ولا تخزن Client Secret. أثناء الانتهاء تتوقف المزامنة وتظل النسخة المحلية محفوظة. احفظ وأغلق Excel، وسجّل الدخول، ثم أعد الاتصال لاستكمال رفع التعديلات المعلقة.

## البديل المتاح فورًا

يمكنك إدخال Fine-grained Token مرة واحدة في الخانة الأصلية مع تفعيل «تذكّر الدخول». بعد نجاح الاتصال يحفظه البرنامج بحماية Windows ويستعيده عند التشغيل التالي حتى ينتهي أو تلغي صلاحيته. إلغاء علامة «تذكّر الدخول» يحذف النسخة المحفوظة؛ يبقى الدخول في ذاكرة الجلسة الحالية فقط.

المصدر الرسمي: https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app
