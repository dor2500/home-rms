# מחקר השוואתי: מה יש בתוכנות השליטה מרחוק הפופולריות שאין עדיין ב-Home RMS (`v1.5.7`)

מסמך זה מסכם מחקר על התוכנות המובילות בשוק השליטה מרחוק וה-RMM:
**TeamViewer**, **AnyDesk**, **RustDesk**, **Parsec**, **Splashtop**, **ConnectWise ScreenConnect**, **MeshCentral**, ו-**Tactical RMM**, ומשווה אותן ליכולות הקיימות כיום ב-**Home RMS (v1.5.7)**.

---

## 1. מה כבר קיים אצלך ב-Home RMS (יתרונות חזקים שכבר מומשו)
לפני פערי הפיצ'רים, חשוב לציין שכבר יש לך בסיס מתקדם מאוד שחלק מהמתחרים גובים עליו כסף או לא מציעים כלל:
- **אבטחת mTLS + חתימת Controller ייחודית (DPAPI + SHA-256 Pinning)** — מניעת חיבור של כל תוכנה זרה.
- **שידור GPU מהיר (`DXGI Desktop Duplication` + `TileStreamer` + `WIC 4:4:4` + `CAS Sharp+`)**.
- **סנכרון לוח דו-כיווני לטקסט ולקבצים (`CF_HDROP`)** כולל סנכרון אוטומטי לפני קליק ימני.
- **פריסה שקטה (`.exe`, `.msi`, `.msp`, `.ps1`, `.cmd`, `.bat` + `winget`)** וניהול מערכת בסגנון Windows 11 (שירותים, Registry, חומת אש, עדכוני Windows, משימות מתוזמנות).
- **PowerShell מרוחק, מנהל משימות, עדכוני OTA דחוסים מ-GitHub, וגילוי אוטומטי ב-LAN.**

---

## 2. פערים מרכזיים: מה יש במתחרים ואין עדיין ב-Home RMS

### א. העברת קבצים וניהול קבצים (File Manager)
| פיצ'ר | אצל מי קיים | מה חסר ב-Home RMS |
| :--- | :--- | :--- |
| **סייר קבצים דו-צדדי (Dual-Pane File Manager)** | TeamViewer, AnyDesk, RustDesk, MeshCentral, ScreenConnect | כיום אפשר להעביר קבצים דרך העתק/הדבק (עד 25MB) או ב-Deploy. חסר מסך ייעודי שמציג במקביל את התיקיות אצלך ואצל הלקוח עם גרירה ושחרור (`Drag & Drop`) דו-כיוונית לקבצים בכל גודל (GBs). |
| **העברת קבצים גדולים בחלקים (Chunked File Transfer + Resume)** | TeamViewer, AnyDesk, RustDesk | כיום `ClipboardFile` מוגבל ל-25MB בזיכרון. חסר מנגנון Streaming של קבצים גדולים עם פס התקדמות, עצירה והמשך. |
| **גרירה ישירה מהמחשב המקומי לתוך חלון השליטה** | TeamViewer, AnyDesk, ScreenConnect | לגרור קובץ מהשולחן עבודה שלך ישר לתוך תיקייה פתוחה אצל הלקוח מרחוק. |

### ב. שידור מסך, וידאו ואודיו (Streaming & Media)
| פיצ'ר | אצל מי קיים | מה חסר ב-Home RMS |
| :--- | :--- | :--- |
| **העברת שמע מרחוק (Audio Streaming + Mic Passthrough)** | TeamViewer, AnyDesk, Parsec, Splashtop, RustDesk | כיום אין שידור שמע (WASAPI Loopback) מהמחשב המרוחק אליך, ואין העברת מיקרופון. |
| **קידוד וידאו חומרתי (`H.264` / `H.265` / `AV1` via NVENC / AMF / QuickSync)** | Parsec, RustDesk, Splashtop | כיום השידור מבוסס על `JPEG Tiles`. מעבר לקידוד וידאו חומרתי יחתוך את רוחב הפס ב-70%–90% ויאפשר 60–120 FPS חלקים בווידאו ובגלילה. |
| **ריבוי מסכים (Multi-Monitor Switching & Multi-to-Multi)** | TeamViewer, AnyDesk, RustDesk, Parsec, Splashtop | כיום לוכדים את המסך הראשי (`PrimaryBounds`). חסרה בחירה בין מסכים (Display 1 / Display 2 / All Monitors) ויצירת מסך וירטואלי למחשבים ללא מוניטור (Headless). |
| **מצב פרטיות (Privacy Mode / Black Screen & Lock Input)** | TeamViewer, AnyDesk, RustDesk, Splashtop, ScreenConnect | להחשיך פיזית את המסך אצל המשתמש (להציג מסך שחור "System under maintenance") ולנעול לו מקלדת/עכבר בזמן עבודה רגישה. |
| **הקלטת סשן לווידאו (Session Recording)** | TeamViewer, AnyDesk, ScreenConnect, MeshCentral | הקלטה אוטומטית או ידנית של סשן השליטה לקובץ `.mp4` לצורכי תיעוד ובקרה. |

### ג. שליטה מרוחקת מתקדמת (Control & UX)
| פיצ'ר | אצל מי קיים | מה חסר ב-Home RMS |
| :--- | :--- | :--- |
| **שליחת `Ctrl+Alt+Del` וטיפול במסכי UAC (Secure Desktop)** | TeamViewer, AnyDesk, RustDesk, ScreenConnect | כאשר ה-Agent רץ כשירות Windows (`LocalSystem`), הוא יכול להזריק `SAS` (`Ctrl+Alt+Del`) ולהישאר בשליטה גם במסך נעילה ומסכי UAC. |
| **הדפסה מרחוק (Remote Printing)** | TeamViewer, AnyDesk, Splashtop | שליחת מסמך מהמחשב המרוחק להדפסה במדפסת המקומית שלך (ולהפך). |
| **צ'אט מובנה + ציור על המסך (Whiteboard / Laser Pointer)** | TeamViewer, AnyDesk, RustDesk, ScreenConnect | חלונית צ'אט מהירה מול המשתמש בזמן תמיכה, או סימון/חץ על המסך להדרכה. |
| **קיצורי מקשים מיוחדים בסרגל (Lock PC, Switch User, Task Manager)** | TeamViewer, AnyDesk, ScreenConnect | כפתורים מהירים בסרגל השליטה לפעולות מערכת מיידיות. |
| **תמיכה בבקרי משחק / USB Passthrough** | Parsec, Splashtop | העברת בקר משחק או התקן USB מרחוק (רלוונטי לגיימינג/עריכה). |

### ד. ארכיטקטורת RMM, גישה מרחוק (WAN) וניהול צי (Fleet)
| פיצ'ר | אצל מי קיים | מה חסר ב-Home RMS |
| :--- | :--- | :--- |
| **חיבור מחוץ לרשת הביתית (WAN / Relay / NAT Traversal)** | TeamViewer, AnyDesk, RustDesk, MeshCentral, ScreenConnect | כיום Home RMS עובד ב-**LAN בלבד** (או דרך VPN כמו Tailscale/WireGuard). לתוכנות הפופולריות יש שרת Relay/Rendezvous לחיבור מכל מקום בעולם בלי לפתוח פורטים בראוטר. |
| **הרצה כשירות Windows (`LocalSystem`)** | TeamViewer, AnyDesk, ScreenConnect, Tactical RMM | כיום `HomeRmsAgent.exe` רץ כתהליך משתמש רגיל (User-level). ריצה כ-Windows Service מאפשרת חיבור עוד לפני שמישהו עשה Login ב-Windows, ובלי חלונות UAC. |
| **הרצת פקודות המונית (1-to-Many Mass Execution)** | Tactical RMM, ScreenConnect, MeshCentral | לבחור כמה מחשבים במקביל ולשלוח לכולם סקריפט או התקנה בבת אחת עם דו"ח הצלחה/כישלון. |
| **Wake-on-LAN (WoL) מובנה** | TeamViewer, AnyDesk, MeshCentral | שמירת כתובת MAC של כל מחשב ושליחת Magic Packet להעיר מחשב כבוי/ישן ישירות מכרטיס המחשב ב-Fleet. |
| **התראות פרואקטיביות (Alerting & Thresholds)** | Tactical RMM, MeshCentral | התראה אוטומטית כשהדיסק מתמלא (>90%), כשה-CPU תקוע על 100%, או כשמחשב יורד מאופליין. |
| **שליטה מדפדפן (Web Console) / אפליקציית מובייל (iOS/Android)** | MeshCentral, TeamViewer, AnyDesk, Splashtop, RustDesk | התחברות ושליטה גם מהטלפון או מדפדפן בלי להתקין תוכנה. |

### ה. אבטחה וביקורת (Security & Auditing)
| פיצ'ר | אצל מי קיים | מה חסר ב-Home RMS |
| :--- | :--- | :--- |
| **נעילה אוטומטית של ממשק ה-Controller (Auto-Lock / PIN / 2FA)** | TeamViewer, Splashtop, Tactical RMM | נעילת ממשק הניהול אוטומטית אחרי X דקות של חוסר פעילות (למניעת שימוש לא מורשה אם עזבת את המחשב פתוח). |
| **אימות חתימה / SHA-256 בעדכוני OTA** | RustDesk, ScreenConnect | אימות קריפטוגרפי שהקובץ שירד מ-GitHub זהה ל-Hash חתום לפני ההחלפה. |
| **Audit Log מרכזי ומוצפן + התראות כניסה** | ScreenConnect, Tactical RMM | לוג פעולות מאובטח שמתעד כל חיבור, העברת קובץ ופקודה, בלי יכולת למחוק אותו בקלות. |

---

## 3. המלצת תיעדוף (Roadmap מוצע ל-Home RMS)
אם תרצה להמשיך לפתח את **Home RMS** לרמה של מוצר מסחרי שלם, זה סדר העדיפויות שיתן את הקפיצה הכי גדולה:
1. **בחירת מסכים (Multi-Monitor Switcher)** + **העברת שמע (WASAPI Audio)** — משדרג מיידית את חוויית השליטה מרחוק.
2. **סייר קבצים דו-צדדי (Dual-Pane File Manager)** לקבצים גדולים ללא מגבלת גודל.
3. **מצב פרטיות (Privacy Mode — מסך שחור אצל הלקוח ונעילת קלט)** + **הקלטת סשן**.
4. **שירות Windows (`LocalSystem`) + Wake-on-LAN** — שליטה מלאה גם לפני Login והדלקת מחשבים מרחוק.
5. **נעילה אוטומטית עם PIN ב-Controller** + **הרצה המונית (1-to-Many)** על כמה מחשבים במקביל.
