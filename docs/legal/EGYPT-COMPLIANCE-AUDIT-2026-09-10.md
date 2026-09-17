# Cocorra — Egypt Legal & Regulatory Compliance Audit

**Date of audit:** 10 September 2026
**Scope:** Egyptian law as applicable to the Cocorra platform, assessed against the actual state of the `cocorra-backend` repository (branch `main`, commit `4d2a46f` plus uncommitted working tree).
**Method:** Full source inspection of controllers, hubs, services, EF Core models, migrations, configuration, container/deploy definitions and existing repository documentation; combined with research into current Egyptian legislation and regulatory instruments.

> **This document is not legal advice.** It is an engineering-and-compliance evidence pack produced to be handed to a qualified Egyptian lawyer. Every conclusion is tagged with its evidentiary basis and, where the legal position is genuinely unsettled, marked `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`. Nothing in this document should be read as a statement that Cocorra is legally compliant.

---

## A note on citation reliability

Egyptian statutes exist officially in Arabic. The English translations in circulation **disagree on article numbering** — for example, one reputable source places the legal bases for processing at PDPL Art. 6 and the Data Protection Centre at Art. 19, while another places consent at Art. 2 and the DPO duties at Art. 8. Where article numbers appear below, they are the numbers given by the cited source. **Article numbers must be re-verified against the Arabic original before being relied on in any filing, contract or policy.** The substance of the obligations is well corroborated across sources; the numbering is not.

---

# Executive Summary

Cocorra is a voice-first, coach-led live-audio social platform for Arabic-speaking users, with mandatory voice-sample verification at signup, direct messaging, friend graphs, reporting/moderation, and a product-analytics pipeline. The backend is ASP.NET Core 10 + SQL Server + self-hosted LiveKit + MinIO, deployed by GitHub Actions to a single VPS.

**The five findings that dominate everything else:**

1. **The single most consequential legal deadline is ~7 weeks away.** Egypt's PDPL (Law 151/2020) sat dormant for five years for want of implementing rules. Those rules — **Executive Regulations issued by MCIT Ministerial Decree No. 816 of 2025 on 1 November 2025** — activated the regime and opened a **12-month transition ending 31 October / 1 November 2026**. From that date the Personal Data Protection Centre ("PDPC") exercises full enforcement powers. Cocorra currently satisfies almost none of the operational requirements.

2. **All personal data appears to leave Egypt, including the primary database.** The production host `152.239.115.176` — which carries the SQL Server instance, the MinIO object store holding voice recordings and profile photos, the API, and the LiveKit media/TURN server — geolocates to **Frankfurt, Germany (Hostinger International, AS47583)**. This is not a peripheral SDK transfer; it is the entire data estate. Under the PDPL cross-border regime this requires a **PDPC licence/permit *and* the data subject's specific informed consent**. Neither exists. (Geolocation is strong evidence, not proof — **verify the contractual server location with Hostinger.**)

3. **Cocorra processes at least three categories of PDPL "sensitive personal data" with no licence, no explicit written consent, and no consent records at all.** The mandatory voice-verification sample (biometric), the `MentalHealth` room category and `MBTI` field (psychological/mental data), and — because nothing prevents minors registering — children's data. Sensitive-data processing requires a PDPC permit/licence plus explicit consent; unlawful disclosure of sensitive data carries fines reported at **EGP 500,000–5,000,000 and imprisonment**.

4. **There is no privacy policy, no terms of service, and no community guidelines anywhere in the repository, and no consent mechanism of any kind.** A repository-wide search for these documents and for any consent, policy-version or acceptance-timestamp field returns nothing. Cocorra therefore cannot demonstrate a lawful basis for any processing, cannot prove what any user agreed to, and cannot satisfy the Executive Regulations' documented-and-auditable consent standard.

5. **Account deletion is broken in a way that defeats the right to erasure.** `DeleteAccountAsync` cleans four tables but leaves `RoomParticipants`, `Reports`, `Rooms` and `TopicVotes` — all of which hold `DeleteBehavior.Restrict` foreign keys to the user. Any user who has ever joined a room or filed a report will therefore hit `DbUpdateException` and receive *"Cannot delete account… Please contact support."* Separately, the deletion path never deletes the stored **voice recording or profile photo** from MinIO, so the biometric sample survives the account indefinitely.

Compounding all of the above: **live production credentials for the database, JWT signing, email, LiveKit, TURN and object storage are committed to the git repository in plaintext**, a fact the repository's own `.gitignore` explicitly acknowledges. Anyone with repository access can mint an admin JWT and read the entire user database, including voice samples. This converts several "process" gaps into an active breach-notification risk.

**Bottom line:** Cocorra should not conduct a public launch on the current implementation. There is, however, no finding in this audit that is inherently unfixable, and none that requires abandoning the product concept. The women-only model, the voice-room architecture and the self-hosted LiveKit choice are all defensible. The gaps are documentation, consent plumbing, licensing, data location and credential hygiene.

---

# PART 1 — Cocorra Legal Product Profile

Determined by source inspection, not by product description.

| Question | Finding | Evidence |
|---|---|---|
| What Cocorra is | Voice-first social platform; Clubhouse/Twitter-Spaces-style live audio rooms hosted by "coaches", for Arabic-speaking users | `README.md:3-5`; `docs/dashboard-discovery/01-product-feature-inventory.md:9` |
| Target users | Arabic-speaking community; coach-led discussion | `01-product-feature-inventory.md:9` |
| **Users exclusively women?** | **Not implemented.** There is no gender field on `ApplicationUser`, no gender in `RegisterDto`, and no gender check anywhere. A repo-wide search for `gender\|female\|woman\|women` in `*.cs` returns **zero** matches. Any women-only positioning is currently marketing-only. | `ApplicationUser.cs`; `RegisterDto.cs`; repo-wide grep |
| Private communication | Yes — 1:1 persistent DMs between accepted friends, plus in-room private messages which are **also persisted** to the same `Messages` table | `ChatHub.cs`; `RoomHub.cs:1004-1022` → `_chatService.SaveMessageAsync` |
| Public/private rooms | Both. `Room.IsPrivate` (default `false`) | `Room.cs:47` |
| Rooms discoverable | Yes — `GET /Api/V1/Room/Feed` | `RoomsController.cs`; `01-product-feature-inventory.md:70` |
| Host/coach functionality | Create/start/end rooms; approve to stage; grant extra time; mute; move to audience; kick | `RoomHub.cs` |
| Participant functionality | Join, listen, raise hand, speak when approved, toggle mic; spoken time tracked | `RoomHub.cs`; `RoomParticipant.TotalSpokenSeconds` |
| Moderation functionality | User reports with category + free-text + optional screenshot; admin review; actions = warn / mute 24h / ban / reject report | `Report.cs`; `SupportService.cs`; `AdminReportAction.cs` |
| Admin functionality | User list/search, status change, bulk status change, dashboard stats, ban + block-all-devices, role management, support chat, report actions | `AdminController.cs`; `AdminService.cs`; `RolesController.cs` |
| User-generated content | Room titles/descriptions, names, bios, profile photos, room images, DMs, support messages, report descriptions, report screenshots, voice samples, live audio | multiple models |
| Audio communication | Yes — WebRTC via **self-hosted** LiveKit + built-in TURN | `livekit/livekit.yaml`; `LiveKitService.cs` |
| **Is live audio recorded?** | **No.** No LiveKit Egress/recording configuration exists in `livekit.yaml`; no egress API is called anywhere in the codebase. | `livekit.yaml`; `LiveKitService.cs` (only `CreateRoom`, `DeleteRoom`, `UpdateParticipant`, `RemoveParticipant`) |
| Is live audio streamed only | Yes — relayed through the self-hosted SFU/TURN, not persisted | as above |
| **Is any audio stored?** | **Yes — the verification sample.** Every registration requires a voice file, stored in MinIO and referenced by `ApplicationUser.VoiceVerificationPath`. This is separate from room audio. | `RegisterDto.cs:33`; `UploadVoice.cs`; `ApplicationUser.cs:15` |
| Chat/messages | Yes, persisted: `Message` (1000-char cap), `SupportMessage`, `SupportTicket` | `Message.cs`; `SupportMessage.cs` |
| Profile information | First/last name, age, MBTI, bio, profile photo, status | `ApplicationUser.cs` |
| Image upload | Yes — profile picture (**required at registration**), room image, report screenshot, support-ticket screenshot | `RegisterDto.cs:35`; `UploadImage.cs`; `Report.ScreenshotPath` |
| Link sharing | Not restricted. Message `Content` is a free 1000-char string; room title/description free text. No URL filtering exists. | `Message.cs:18` |
| Reporting/blocking | Reports (5 categories); user-to-user blocking (`UserBlock`) | `Report.cs`; `UserBlock.cs`; `BlockController.cs` |
| Device blocking | Yes — `BlockedDevices` doubles as a **device registry** (written on every login/refresh) and a blocklist; `DeviceBlockingMiddleware` enforces on `IsBlocked=true` | `BlockedDevices.cs`; `DeviceBlockingMiddleware.cs`; `AuthServices.cs:635` |
| Account suspension | Yes — `UserStatus.Banned` + Identity lockout to `DateTimeOffset.MaxValue`; refresh + FCM tokens cleared; re-checked on **every** request via `OnTokenValidated` | `AdminService.cs:421-478`; `Program.cs:360-373` |
| Account deletion | Endpoint exists but is **functionally broken** — see Part 23 | `AuthServices.cs:539-597` |
| **Age restrictions** | **None enforced.** `RegisterDto.Age` is `[Required] int` with **no `[Range]`**, no minimum, no date of birth, and no verification. `Age = 8` is accepted. | `RegisterDto.cs:18-19`; `ApplicationUser.cs:14` |
| Can minors register | **Yes.** Nothing prevents it. | as above |
| Payments/subscriptions | **None.** No payment provider, no price, no subscription model, no invoice logic anywhere in the repository. | repo-wide inspection; `*.csproj` dependency list |
| Advertising | None implemented | — |
| Promotions/referrals | None implemented | — |
| Future monetization | Not documented anywhere in the repository | — |

**Sensitive-content posture worth flagging early:** `RoomCategory` is `Relationships`, `MentalHealth`, `Others` (`RoomCategory.cs`). A platform whose *categories* invite disclosure of mental-health and relationship matters, in Arabic, by users the state has recently prosecuted for online speech (see Part 8/22), is a materially higher-risk product than a generic audio app.

---

# PART 2 — Company / Business Structure

**Evidence found in the repository is almost entirely absent.** This section deliberately records "not found" rather than inferring.

| Item | Evidence Found | Legal Requirement | Status | Risk |
|---|---|---|---|---|
| Company name | None. Only the product name "Cocorra" and domain `cocorraapp.com` | A commercial entity carrying on business in Egypt must be registered | **UNKNOWN** | HIGH |
| Trade name | "Cocorra" used throughout; no registration evidence | Trade-name registration in the Commercial Register | **UNKNOWN** | MEDIUM |
| Legal entity / form | Not found | LLC / SAE / OPC / sole proprietorship | **UNKNOWN** | HIGH |
| Founders | Git author `Kareem`; `JWTSetting:securityKey` contains the literal string `ByKareem24031977` (`appsettings.json:20`) | — | Partial, informal | INFORMATIONAL |
| Ownership | Not found | Shareholding recorded at GAFI/Commercial Register | **UNKNOWN** | HIGH |
| Registered company / commercial registration | Not found | GAFI incorporation → Commercial Register certificate (*Sijil Tijari*) | **UNKNOWN** | HIGH |
| Tax registration | Not found | Tax card / TIN from the Egyptian Tax Authority | **UNKNOWN** | HIGH |
| VAT status | Not found; no VAT logic in code | VAT registration once thresholds/activity trigger it (Part 14) | **N/A while free** | MEDIUM (on monetization) |
| Business activity | Software/app operation; audio social networking | Activity must be within the registered objects | **UNKNOWN** | MEDIUM |
| Address | Only `cocorraapp.com` and a **German** server IP | Registered office required; consumer-facing disclosure required (Part 11) | **NOT FOUND** | HIGH |
| Contact information | `cocorra02@gmail.com` (`appsettings.json:27,32`) — a personal Gmail account used as both SMTP sender and seeded admin login | A controller must publish a contact point; PDPC registration requires entity details | **INADEQUATE** | HIGH |
| Payment entity | None | — | N/A | — |
| Bank / payment provider | None | — | N/A | — |

**Why this matters legally, not just administratively.** Three separate Egyptian regimes key off corporate registration:

1. **PDPL licensing.** The Executive Regulations require a **copy of the commercial register** as part of the licence/permit application for legal persons. *No commercial registration ⇒ no PDPC licence ⇒ no lawful sensitive-data processing and no lawful cross-border transfer.* Corporate formation is therefore a **hard prerequisite**, not a parallel workstream.
2. **Consumer Protection Law 181/2018.** A supplier contracting remotely must disclose, pre-contract, its identity, contact details, **commercial registration number and tax card** (Part 11).
3. **Tax.** Corporate income tax and (on monetization) VAT presuppose a registered taxpayer.

**Must be verified with an Egyptian corporate lawyer / accountant:** whether an entity already exists; whether the current operating arrangement constitutes unregistered commercial activity; whether an OPC or LLC is preferable; whether the ITIDA/technology-activity route offers advantages; and who is currently the legal data controller — because on the present evidence **no legal person is identifiable as controller**, which is itself a PDPL problem.

`LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED` — the entire section.

---

# PART 3 — Personal Data Protection Audit (Law 151/2020 + ER 816/2025)

## 3.1 Legal instrument status — verified current

| Instrument | Status as at 10 Sep 2026 | Source |
|---|---|---|
| **Law No. 151 of 2020** (PDPL) | In force. Enacted 2020; applies to electronic processing of personal data of natural persons. | [DLA Piper](https://www.dlapiperdataprotection.com/?t=law&c=EG) |
| **Ministerial Decree No. 816 of 2025** (Executive Regulations) | **In force.** Issued by the Minister of Communications and Information Technology on **1 November 2025**, published in the Official Gazette, effective the following day. | [Al Tamimi](https://www.tamimi.com/law_update_articles/from-policy-to-practice-egypt-issues-executive-regulations-of-the-personal-data-protection-law/); [Clyde & Co](https://www.clydeco.com/en/insights/2026/01/egypt-regulatory-update-on-data-privacy) |
| **Transition period** | **12 months from issuance → full enforcement from 31 October / 1 November 2026.** | [Clyde & Co](https://www.clydeco.com/en/insights/2026/01/egypt-regulatory-update-on-data-privacy); [Lexology](https://www.lexology.com/library/detail.aspx?g=334b6157-bbeb-474d-ba67-11140e89cc07) |
| **Personal Data Protection Centre (PDPC)** | Established and confirmed as supervisory authority; issues licences and registrations; electronic portal for applications. | [Access Partnership](https://accesspartnership.com/opinion/egypt-finalises-executive-regulations-to-the-personal-data-protection-law-pdpl/); [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt) |
| **Extraterritorial reach** | Applies to organisations processing personal data **connected to Egypt regardless of physical presence** in Egypt. | [Clyde & Co](https://www.clydeco.com/en/insights/2026/01/egypt-regulatory-update-on-data-privacy) |

**Consequence for Cocorra:** hosting in Germany does **not** take Cocorra outside the PDPL. It brings Cocorra *inside* the PDPL as a controller of Egyptian data subjects' data **and** creates a cross-border transfer to be licensed.

## 3.2 Controller / processor characterisation

| Activity | Cocorra's role | Reasoning |
|---|---|---|
| Accounts, profiles, voice verification, rooms, messages, reports, analytics | **Controller** | Cocorra determines purposes and means; the data model and event taxonomy are Cocorra's own design |
| Hosting/storage on the VPS, MinIO, SQL Server | **Controller**, with the VPS provider (Hostinger) as **processor** | Infrastructure operated for Cocorra's purposes |
| Live audio via self-hosted LiveKit | **Controller** — no third-party processor involved for media | LiveKit is self-hosted on Cocorra's own server (`livekit.yaml`); it is Cocorra's own software, not LiveKit Cloud |
| FCM push delivery (Google) | **Controller**, Google as **processor** | Cocorra pushes payloads containing message text |
| Transactional email (Gmail SMTP) | **Controller**, Google as **processor** | OTP and notification email |
| Support-chat content, moderation records | **Controller** | — |

Cocorra is **not** currently acting as a processor for anyone else. There is no scenario in the codebase where Cocorra processes on another controller's instructions.

## 3.3 Data category inventory and per-category legal analysis

Fifteen questions are asked of each category, per the audit brief. `NO` answers are the gap list.

Legend: **PD** = personal data · **SPD** = sensitive personal data under PDPL Art. 1 · storage location for *everything* below is the **Frankfurt VPS** unless stated.

---

### (1) First name / last name
`ApplicationUser.FirstName/LastName`, required, 50 chars.
1. PD? **Yes.** 2. Sensitive? No. 3. Why: identity, display in rooms and profiles. 4. Purpose defined? **No** — nowhere documented to the user. 5. Lawful basis? Arguably contract performance, but **undocumented and unstated**. 6. Consent required? Not if contract basis holds. 7. Explicit consent? No. 8. Minimised? **Questionable** — a voice-room pseudonym platform does not obviously need legal names; there is no username field, so real names are the display identity. 9. Retention? **Undefined.** 10. Rights exercise? Update via `PUT /api/Profile/update`; deletion broken (Part 23). 11. Where: SQL Server, Frankfurt. 12. Access: user, other users (public profile), all Admins **and Coaches** (`AdminController.cs:17`). 13. Outside Egypt? **Yes.** 14. Licence? Controller/processor licence + cross-border authorisation. **None.** 15. Controls: TLS in transit at the edge; **no encryption at rest**; `sa` credentials in git.

### (2) Email address
Identity `Email`, unique required; also `SupportTicket.ContactEmail`.
1. PD? **Yes.** 2. SPD? No. 3. Login, OTP, password reset, ban notification. 4. Purpose defined? **No.** 5. Basis: contract — undocumented. 6–7. n/a / **No**. 8. Minimised: yes. 9. Retention: **undefined**; `SupportTicket.UserId` is `SetNull` on deletion but **`ContactEmail` is not cleared**, so an email address survives account deletion inside support tickets (`AppDbContext.cs:170-174`; `SupportTicket.cs:19`). 10. Rights: no self-service export; deletion broken. 11. SQL Server, Frankfurt. 12. Admins + Coaches. 13. **Yes** — and additionally to **Google** via Gmail SMTP for every OTP. 14. Cross-border authorisation required. **None.** 15. No field-level protection.

### (3) Password / authentication information
ASP.NET Core Identity `PasswordHash`.
1. PD? Yes. 2. SPD? No. 3. Authentication. 4–5. Contract; undocumented. 8. Minimised: yes. 9. Retention: life of account. 12. **No human access** (hashed — Identity default PBKDF2-HMAC-SHA512, 100k iterations). 13. Yes (DB in Germany). 15. **Adequate hashing** — one of the genuinely sound controls. **But** `RegisterDto` requires only `MinLength(6)` (`RegisterDto.cs:24`) while `Program.cs:313` sets `RequiredLength = 8`; the DTO is looser than Identity, so the DTO annotation is misleading rather than harmful. **Adverse finding:** `PasswordResetDto` exists under `AdminDto` — admin-initiated password reset needs review for whether an admin can set a known password and impersonate a user (`Cocorra.DAL/DTOS/AdminDto/PasswordResetDto.cs`).

### (4) Profile data — bio, profile photo, room image
`Bio`, `ProfilePicturePath` (**required at registration**), `Room.ImagePath`.
1. PD? **Yes** — a facial photograph is PD; PDPL Art. 1 expressly names "picture". 2. SPD? **Photograph is not automatically biometric**, but if it were ever used for identification/matching it becomes biometric ⇒ SPD. Currently it is not. 3. Display. 4. **Not defined.** 5. Consent/contract — undocumented. 8. **Not minimised** — a profile picture is `[Required]` to register (`RegisterDto.cs:35`); a voice platform compelling a photograph is hard to justify as necessary, and for a women-only platform in Egypt it is a *safety* liability as well. 9. Retention: **indefinite — file is never deleted on account deletion**. 11. **MinIO, served from `https://storage.cocorraapp.com` as a public URL** (`UploadVoice.cs:60`; `MinioSettings.PublicUrl`). 12. **Potentially anyone with the URL** — no code sets a bucket policy or generates pre-signed URLs, and the client renders the returned absolute URL directly, which implies anonymous public read. **VERIFY the MinIO bucket policy on the server.** 13. Yes. 14. Licence + cross-border authorisation. **None.** 15. **MinIO reached over plain HTTP** (`Program.cs:282` `UseHttp = true`; endpoint `http://152.239.115.176:9000`).

### (5) Gender
**Not collected.** No field, no DTO, no validation. See Part 6 — this is the central contradiction in the women-only proposition.

### (6) Age / date of birth
`ApplicationUser.Age` (`int`), `RegisterDto.Age` `[Required]` with **no range**.
1. PD? Yes. 2. **SPD if the user is a child** — the ER treat data relating to children as sensitive, and require **explicit written guardian consent for under-15s**, or consent from the child or guardian for **15–18**. 3. Why collected: unclear; not used for gating. 4. **No purpose defined.** 5. **No lawful basis for processing a minor's data at all**, since no guardian consent mechanism exists. 6–7. **Explicit written guardian consent required and absent.** 8. Minimised: an integer age is *more* minimal than a DOB — but it is also **unverifiable and self-asserted**, so it cannot support any age gate. 9. Retention: life of account. 14. **Children's data is sensitive ⇒ PDPC permit/licence required.** None. 15. **No control whatsoever.**
> Source: [Al Tamimi](https://www.tamimi.com/law_update_articles/from-policy-to-practice-egypt-issues-executive-regulations-of-the-personal-data-protection-law/) (via search summary); [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt).

### (7) MBTI personality type
`ApplicationUser.MBTI`, submitted during onboarding (`SubmitMbtiDto`).
1. PD? **Yes.** 2. **SPD? Arguably yes.** PDPL Art. 1 lists data relating to **psychological or mental** state among sensitive categories. An MBTI type is a self-reported psychological profile attribute. Whether a four-letter personality code meets the statutory threshold for "psychological data" is genuinely arguable. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`. 3. Why: onboarding/matching flavour. 4. **Purpose not defined.** 5. **No basis** — it is not necessary for the service. 6–7. If SPD: **explicit written consent + PDPC permit.** Neither exists. 8. **Not minimised** — nothing in the product appears to use it. 9. Indefinite. 15. None.
> **Cheapest available mitigation: delete the field.** If it drives no feature, it is pure liability.

### (8) Voice verification recording — **HIGHEST-RISK CATEGORY**
`ApplicationUser.VoiceVerificationPath`; file in MinIO; `[Required]` at registration; reviewed manually by an admin; re-recordable on `ReRecord` status.
1. PD? **Yes, expressly** — PDPL Art. 1's definition of personal data names **"voice"**. 2. **SPD? Yes — treat as biometric.** The recording's *sole purpose* is to verify the identity/characteristics of the person. Biometric data is enumerated as sensitive. 3. Why: gate access; admin manually approves `Pending → Active`. 4. Purpose defined **internally** but **never disclosed to the user**. 5. **No documented basis; consent not obtained.** 6. **Explicit consent required.** 7. **Explicit *written* consent required for SPD.** 8. Minimisation: a ≤3 MB audio sample retained indefinitely, when the stated purpose (one-off human review) is complete within days. **Fails minimisation and storage limitation.** 9. Retention: **indefinite, and survives account deletion** — `DeleteAccountAsync` never calls `_uploadVoice.DeleteVoice` (contrast `AuthServices.cs:506`, which *does* delete on re-record). 10. Rights: no access/export path; deletion does not delete it. 11. **MinIO on the Frankfurt VPS, exposed via a public URL.** 12. **Every Admin, and the URL is likely anonymously fetchable.** 13. **Yes — transferred out of Egypt.** 14. **Requires a PDPC licence/permit for sensitive-data processing AND a cross-border transfer authorisation.** Neither exists. 15. Content-type + magic-byte signature validation (`UploadVoice.cs:106-147`) — that is *upload hygiene*, not data protection. **No encryption at rest, plain-HTTP transport to MinIO, no access control, no retention limit, no deletion.**

> **This single category, standing alone, is the audit's most serious exposure.** Sensitive-data mishandling is reported to attract **EGP 500,000–5,000,000 and a minimum term of imprisonment**, and cross-border transfer without approval is separately criminalised. Sources: [Global Compliance News](https://www.globalcompliancenews.com/2020/11/08/egypt-and-united-arab-emirates-egypt-issues-new-data-protection-law28092020/); [Kennedys](https://www.kennedyslaw.com/en/thought-leadership/article/2026/egypt-s-personal-data-protection-law-the-compliance-countdown-has-begun/).

### (9) Live room audio (transmitted, not stored)
See Part 5 for the full treatment. **Not stored** anywhere; relayed by self-hosted LiveKit/TURN on the Frankfurt host. Still personal data **in transit**, still processed abroad, still requires disclosure.

### (10) Device identifiers and device metadata
`BlockedDevices.DeviceId/DeviceName/DeviceModel/DeviceType/DeviceOs`, from `X-Device-*` headers, written on **every login and refresh** (`AuthServices.cs:635`).
1. PD? **Yes** — PDPL Art. 1 names "online identifier". 2. SPD? No. 3. Why: ban evasion prevention; enforcement via `DeviceBlockingMiddleware`. 4. Purpose defined **in code comments only**. 5. **Legitimate interest is a plausible basis** — this is the strongest legitimate-interest case in the product. Still undocumented and undisclosed. 6. Consent: not required if legitimate interest, **but must be disclosed**. 8. Minimised: reasonable — one row per (user, device), unique index prevents growth (`AppDbContext.cs:214-217`). 9. Retention: **undefined**; rows are `Cascade`-deleted with the user (`AppDbContext.cs:200-204`). 12. Admins/Coaches. 13. Yes. 15. Stored raw, unhashed.
> **Note the product/legal tension:** because device rows cascade on user deletion, a banned user who then deletes their account **destroys the very block that prevented their return**. That is a moderation-effectiveness bug *and* it means the "we keep device IDs to stop ban evasion" justification is undercut by the implementation. See Part 17.

### (11) IP address
Never stored raw. `UserEvent.IpHash` = salted hash; salt is a hard startup requirement with no fallback (`Program.cs:231-246`); IP is also the rate-limit partition key **in memory only** (`Program.cs:406`).
1. PD? A raw IP is PD. **A properly salted hash with a secret, rotated salt is pseudonymised data** — still personal data under most readings, but materially lower risk. 2. SPD? No. 3. Abuse detection/analytics. 5. Legitimate interest — undisclosed. 9. **`RawEventRetentionDays: 180`** (`appsettings.json:86`) with `EventCleanupService` enforcing it — **the only concrete retention period implemented anywhere in the product.** 15. **This is the best-engineered privacy control in the codebase** and deserves to be said plainly: refusing to boot without a salt, and documenting why, is exactly right.
> **Caveat:** 180 days for IP-hash retention happens to align with Law 175/2018 Art. 2's 180-day figure — but see Part 9; that alignment appears coincidental rather than designed, and the *categories* the cybercrime law contemplates are not what `UserEvent` stores.

### (12) User-Agent string
`UserEvent.UserAgent`, 256 chars.
PD? In combination, yes (fingerprinting surface). Purpose undefined. Basis undocumented. Retention 180 days (inherits event retention). Minimisation: **questionable** — full UA strings are rarely needed; a parsed platform/version would suffice.

### (13) Logs (application / audit)
Docker `json-file`, `max-size 10m`, `max-file 3` (`docker-compose.yml:51-55`); optional structured file sink (`Analytics:StructuredLogPath`, unset by default).
**Adverse finding — PII in logs.** `LiveKitService.LogIssuedToken` logs, at `Information` level, the LiveKit **participant name** and user identity for every token issued (`LiveKitService.cs:82-98`), and is itself labelled *"TEMPORARY AUDIT LOGGING — remove once the stage/mic investigation is done."* Temporary diagnostic logging of identity data has been left in the production path. Retention is whatever Docker's rotation yields — **undefined in policy terms**. No log-access control is documented.

### (14) Location data
**Not collected.** No geolocation field, no coarse-location capture, no location permission requested server-side. Positive finding.

### (15) Behavioural / usage data and analytics
`UserEvent` (event type, `PropertiesJson`, `SessionId`, `RoomId`, `CorrelationId`, `OccurredAtUtc`), plus `RoomParticipant.TotalSpokenSeconds`, plus daily aggregate read models.
1. PD? **Yes** — `UserId`-keyed behavioural records. 2. SPD? **Potentially, by inference:** a `room_joined` event whose room has `Category = MentalHealth` links an identified user to a mental-health context. That inference chain is exactly what the sensitive-data rules exist to catch. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`. 3. Product analytics. 4. Purpose **well documented internally** (`USER_TRACKING_PLAN.md`, `docs/dashboard-discovery/*`) — but **never disclosed to users**. 5. **No basis** — product analytics is not necessary to perform the contract, so **consent is required and absent**. 6. **Yes, consent required.** 8. Minimisation: the model has genuine discipline — `PropertiesJson` is documented "NEVER store message bodies, emails, or other PII here" (`UserEvent.cs:26-28`) and IPs are hashed. Credit where due. 9. **180 days** for raw events; **aggregates appear to be retained indefinitely** (no cleanup for `Daily*Metrics`) — acceptable only if genuinely non-identifying, which `DailyHostMetrics` (keyed by `HostId`) is **not**. 10. No access/export/objection path. 12. Admins/Coaches via `AnalyticsController`. 13. Yes. 15. As per DB.
> **Mitigating fact:** `EnableNewEventEmission` and `EnableHighFrequencyEvents` are both **`false`** in `appsettings.json:78-79`, so the expanded event set is not currently emitting. The window to get consent in place *before* switching them on is open. Do not flip them first.

### (16) Session identifier (cookie)
`CocorraSessionId`, `HttpOnly`, `Secure`, `SameSite=Strict`, 7 days, set on **every request** (`SessionTrackingMiddleware.cs:25-34`).
**Adverse finding.** This cookie exists to group analytics events (`session_started`) — an **analytics purpose, not a strictly-necessary one**. The ER recognise implied consent only where processing is *"strictly necessary to deliver a lawful service or transaction expressly requested by the data subject."* An analytics session cookie set with no notice and no consent does not meet that. The cookie *flags* are exemplary; the *lawfulness* is the problem.

### (17) Moderation data — reports
`Report`: `ReporterId`, `ReportedUserId`, `ReportedRoomId`, `Category`, free-text `Description`, `ScreenshotPath`, status, `ResolvedAt`.
1. PD? **Yes, for both reporter and reported.** 2. **SPD? Frequently, in substance.** A free-text harassment report on a mental-health platform will routinely contain health, sexual, or religious detail — a `Harassment` report is very likely to describe sexual content. The *field* is not typed sensitive; the *content* will be. 3. Safety/moderation. 4. Purpose undocumented to users. 5. **Legitimate interest / legal obligation is arguable and reasonably strong**, but must be disclosed. 8. Free-text is inherently unminimised; unavoidable for the purpose. 9. **No retention period.** Reports persist forever. 10. **No subject access for the reported user, no appeal mechanism** (see Part 22). 11. SQL Server + MinIO (screenshots, public URL pattern). 12. **Admins and Coaches** — a Coach is an ordinary platform user with elevated role, and `AdminController`'s class-level attribute is `[Authorize(Roles = "Admin,Coach")]` (`AdminController.cs:17`). See Part 24. 13. Yes. 15. None specific.
> **Erasure conflict:** `Report.ReporterId` is `Restrict`, so a user who has ever filed a report **cannot be deleted** (Part 23). `ReportedUserId` is `SetNull`, so the *accused* is anonymised on their own deletion — which quietly destroys moderation evidence.

### (18) Blocked users / blocked devices
`UserBlock` (`BlockerId`, `BlockedId`, `BlockedDeviceId`); `BlockedDevices.IsBlocked/BlockedAt`.
PD? Yes. SPD? No. Basis: legitimate interest (safety) — undisclosed. Retention undefined. `UserBlocks` **are** deleted on account deletion (`AuthServices.cs:572-574`); device rows cascade. Access: Admins/Coaches.

### (19) Authentication tokens
`ApplicationUser.RefreshToken` (32 random bytes, base64) + `RefreshTokenExpiryTime` (7 days); JWT access token (1 day per `Program.cs` comment).
**Adverse findings:** (a) the refresh token is stored **in plaintext in the users table** — a database read yields directly usable session credentials; (b) refresh tokens are **not rotated-and-revoked on reuse detection**, only replaced; (c) refresh lookup is `SingleOrDefaultAsync(u => u.RefreshToken == dto.RefreshToken)` — an unindexed full-table scan on a secret, and a timing-comparison on a secret value (`AuthServices.cs:609-610`). (d) **The JWT signing key is committed to git** — see Part 24; with it, anyone can forge an `Admin` token.

### (20) Firebase / FCM identifiers and push content
`ApplicationUser.FcmToken`; `PushNotificationService` sends via FirebaseAdmin; `MessagePreview.ForNotificationBody` puts **the actual message text** into the notification body.
1. PD? **Yes** — device-linked identifier, plus **message content**. 2. SPD? Content-dependent; a DM on this platform may well be. 3. Push delivery. 4. Undisclosed. 5. **Consent required for push notifications; none recorded.** 8. **Not minimised** — the design deliberately forwards message text verbatim so a closed app shows readable content (`MessagePreview.cs:5-17`). That is a good *UX* decision with a real *privacy* cost. 12. **Google.** 13. **Yes — to Google infrastructure outside Egypt.** 14. **Cross-border authorisation required.** None. 15. Cleared on ban and on revoke (`AdminService.cs:440`; `AuthServices.cs:655-659`) — good hygiene.

### (21) LiveKit identifiers
LiveKit participant `identity` = the user's `Guid`; `name` = participant display name; room name = room `Guid`; grants and TTL in the JWT.
PD? Yes (pseudonymous identifier + display name). **Positive finding: identity is a GUID, not an email or phone.** Self-hosted, so no third-party processor. Logged at `Information` level (see (13)). Token TTL 240 min.

### (22) Payment information
**None collected. None stored.** Best possible posture. See Part 13 for the forward-looking checklist.

### (23) Support communications
`SupportTicket` (message, `ContactEmail`, screenshot — **anonymous submission permitted**), `SupportChat`, `SupportMessage`.
PD? Yes. SPD? Content-dependent — support tickets on a mental-health-adjacent platform will contain sensitive disclosures. Retention: **none defined**. `SupportTicket.UserId` `SetNull` on deletion **but `ContactEmail` retained** — a de-facto identifier survives erasure. Access: Admins.

### (24) Admin audit logs
**Adverse finding — there is no admin audit log.** Administrative actions are recorded only as `UserEvent` rows via `_eventTracker.Track(...)` with `changedByAdminId` in `PropertiesJson` (`AdminService.cs:451-461`). Three problems: (a) `IEventTracker` is **fire-and-forget with a bounded channel that drops on overflow** — an audit trail that may silently discard entries is not an audit trail; (b) `EnableNewEventEmission` is **`false`**, so depending on which events are gated, some admin attribution may not be emitting at all; (c) `RawEventRetentionDays: 180` means admin action history **self-deletes after six months**. There is no separate, durable, append-only record of who banned whom, who read which voice recording, or who accessed which report.
> This is a compliance-relevant gap in its own right: the ER require registers of data-subject requests and of decisions on legally retained data, structured for regulatory inspection.

---

## 3.4 Cross-cutting PDPL gap summary

| Obligation (PDPL + ER 816/2025) | Cocorra status | Evidence |
|---|---|---|
| Lawful basis identified per purpose | **ABSENT** | no policy, no basis register |
| Explicit consent where required | **ABSENT** | no consent field, flow, or record anywhere |
| Consent documented, auditable, showing form/timing/scope/validity | **ABSENT** | repo-wide grep: no `consent`, `acceptedAt`, `policyVersion` |
| Transparency / notice at point of collection | **ABSENT** | no privacy policy exists |
| Purpose limitation | **NOT DOCUMENTED** | — |
| Data minimisation | **FAILS** for profile photo (required), MBTI, indefinite voice sample, full UA |
| Retention periods pre-determined per category | **ABSENT except events (180d)** | `appsettings.json:86` |
| Deletion on expiry + notify data subject | **ABSENT** | — |
| Data subject rights operable | **PARTIAL / BROKEN** | Part 18, Part 23 |
| Security measures per PDPC standards | **FAILS** | Part 24 |
| Staff confidentiality undertakings | **NO EVIDENCE** | Part 27 |
| DPO appointed in writing, trained, registered | **ABSENT** | no reference anywhere |
| Controller/processor licence | **ABSENT** | Part 2 blocks it |
| Sensitive-data licence/permit | **ABSENT** | voice, mental-health, children |
| Cross-border transfer licence + consent | **ABSENT** | Part 16 |
| Breach register + 72h notification capability | **ABSENT** | Part 25 |
| Records/registers for inspection | **ABSENT** | — |
| E-marketing licence | **N/A** — no marketing sends | positive |
| Local representative (if no Egyptian presence) | **UNKNOWN** | depends on Part 2 |

---

# PART 4 — Consent Audit

**Finding: Cocorra has no consent mechanism of any kind.** This is not "weak consent" or "bundled consent" — it is the total absence of the mechanism. A repository-wide regex search across all `*.cs` for `consent|privacy|terms.?of.?service|policy.?version|acceptedAt|eula` returns exactly **one** hit: a code comment in `UserEvent.cs:42` referring to "Privacy §4", which points to `USER_TRACKING_PLAN.md` — an internal engineering document, not a user-facing policy.

| Consent point | Implemented? | Required treatment | Gap |
|---|---|---|---|
| Registration | **No** | Contractual acceptance of ToS — clickwrap, recorded | No acceptance captured; `RegisterDto` has no acceptance field |
| Onboarding | **No** | Layered notice at collection point (ER: rights must be communicated **at collection**, not only in a general notice) | Absent |
| Privacy policy acceptance | **No** | Separate acknowledgement + version + timestamp | Absent |
| Terms acceptance | **No** | Clickwrap, version-pinned | Absent |
| Marketing consent | N/A | Separate, explicit, purpose-specific, own licence | No marketing exists — keep it that way until licensed |
| Push notification consent | **No** (server-side) | Consent; OS prompt is **not** PDPL consent | `UpdateFcmToken` accepts a token with no consent record |
| Microphone permission | Client-side only | OS permission ≠ PDPL consent for voice processing | No server-side record |
| Camera | N/A | — | — |
| Location | N/A — not collected | — | positive |
| Analytics consent | **No** | **Explicit consent required** (not strictly necessary) | Cookie + events run unconditionally |
| Third-party SDK consent | **No** | Disclosure + consent for Google/FCM transfer | Absent |
| **Voice-sample consent** | **No** | **Explicit *written* consent (sensitive/biometric) + PDPC permit** | Most serious gap |
| Recording consent | N/A — no recording | — | see Part 5 |

**Against the eleven ER-derived tests in the brief:**

| Test | Result |
|---|---|
| Separates contractual acceptance from optional consent | **No** — neither exists |
| Records consent | **No** |
| Stores consent timestamp | **No** |
| Stores policy version | **No** |
| Can prove which version a user accepted | **No** — and this is the ER's central demand: consent must be *"documented, auditable, demonstrating form, timing, scope, and validity"* and verifiable *at any inspection* |
| Allows withdrawal | **No** mechanism |
| Handles withdrawal correctly | **N/A** |
| Avoids bundled consent | **N/A** (nothing to bundle) |
| Avoids preselected consent | **N/A** |
| Distinguishes necessary from optional processing | **No** |
| Guardian consent for minors | **No** — and minors can register |

**Engineering shape of the fix** (for scoping only — not to be implemented during this audit): a `UserConsent` table keyed by user, with `ConsentType`, `PolicyVersion`, `GrantedAtUtc`, `WithdrawnAtUtc`, `Method`, `EvidenceBlob`; an immutable `PolicyVersion` table holding the exact published text hash; acceptance captured in `RegisterDto` as discrete required/optional flags; a withdrawal endpoint; and enforcement so that `EventTracker.Track` and the session cookie are **no-ops without analytics consent**. Note the ER also require that **consent mechanisms be approved by the Centre** — so the design should be settled *before* the licence application, not after.

---

# PART 5 — Audio / Voice Data (HIGH PRIORITY)

## 5.1 Established facts — what the code actually does

| Question | Answer | Evidence |
|---|---|---|
| Is audio transmitted? | **Yes** — WebRTC audio between participants | `livekit.yaml`; `RoomHub` |
| Is room audio stored? | **No** | No egress config; no egress API call |
| Is room audio recorded? | **No** by the platform | as above |
| Temporarily buffered? | **Yes, inherently** — an SFU and a TURN relay hold packets in memory transiently. Not persisted to disk. | `livekit.yaml` `rtc`/`turn` |
| Processed by LiveKit? | **By self-hosted LiveKit software**, on Cocorra's own server | `livekit.yaml`: keys, `turn.enabled: true`, ports 7880/7881/50000-60000/3478/5349 |
| Does LiveKit (the company) store anything? | **No** — this is **not** LiveKit Cloud. Media never reaches LiveKit Inc. | `LiveKitService` targets `wss://live.cocorraapp.com`; `livekit.yaml` is a self-host config |
| Does the server receive raw audio? | **Yes.** `ice_lite: true` + built-in TURN + SFU topology means **all media traverses the Cocorra server**. There is no peer-to-peer path. | `livekit.yaml:82,85-101` |
| Can administrators access audio? | **Live room audio:** not through any application feature — but anyone with **root on the VPS or the LiveKit API secret** could add an egress/recording configuration and capture everything, with no application-level trace. The API secret is **in the git repository**. **Verification samples:** yes, by design, and via a public URL. | `livekit.yaml:12`; `appsettings.json:46` |
| Are recordings possible? | **Yes, trivially** — LiveKit Egress is a config change away | — |
| Can hosts record? | Not via the platform | — |
| Can participants record on their devices? | **Yes, unavoidably.** Any user can screen-record or use an external recorder. | inherent |
| Is recording prohibited contractually? | **No** — there are no terms at all | Part 20 |
| Audio-based moderation? | **No.** Moderation is entirely report-driven and post-hoc; nothing inspects audio. | `Report`; `SupportService` |
| Voice-related identifiers stored? | **Yes** — `VoiceVerificationPath`; plus `TotalSpokenSeconds` per participant per room, and `mic_activated` / `speaking_time_logged` events | `ApplicationUser.cs:15`; `RoomParticipant`; `EventTypes` |

## 5.2 Egyptian legal implications

**(a) Voice is expressly personal data.** PDPL Art. 1's definition of personal data enumerates **"name, voice, picture, identification number, online identifier"** ([DLA Piper](https://www.dlapiperdataprotection.com/?t=law&c=EG)). There is no argument to be had about whether voice is in scope — it is named in the statute.

**(b) The verification sample should be treated as biometric ⇒ sensitive.** Biometric data is an enumerated sensitive category. A voice recording collected and reviewed **for the purpose of verifying who the person is** is functionally biometric regardless of whether an algorithm processes it. Consequences: **explicit written consent**, **a PDPC licence/permit**, data minimisation, secure electronic records, and — because it sits in Germany — **a cross-border authorisation**. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED` on whether human-reviewed (non-algorithmic) voice verification is "biometric" in the Egyptian regulator's construction. **Plan for the worse reading**; the downside is criminal.

**(c) "We don't store room audio" reduces obligations; it does not remove them.** Transmission is processing. Obligations that persist regardless:
- **Disclosure** — users must be told audio is relayed through Cocorra's servers, where those servers are, and that it is not recorded.
- **Security** — the media path must be protected (Art. 4 security duty).
- **Cross-border** — the relay is in Germany, so *live audio content leaves Egypt in real time*. This is a transfer even though nothing is persisted.
- **Integrity of the "not recorded" claim** — a marketing or in-product statement that audio is never recorded becomes a **consumer-protection misrepresentation** the moment the architecture could do otherwise without notice (Part 28). It is a promise about a config file.

**(d) Unauthorised recording by users.** Egypt criminalises privacy-invading conduct: **Law 175/2018 Art. 25** covers unlawful disclosure/use of personal data and privacy-violating conduct without consent, and **Art. 26** provides enhanced penalties for IT-enabled misuse harming reputation or dignity ([Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt)). A participant who records another member's voice-room disclosure and republishes it may commit an offence. **Cocorra's exposure is different in kind:** it will not usually be the recorder, but it can be criticised for failing to prohibit, warn, detect or act. With **no terms, no community guidelines and no report category for "recorded/shared my private conversation"** (`ReportCategory` has only `InappropriateContent`, `Harassment`, `Spam`, `FakeIdentity`, `Other`), Cocorra currently has neither the contractual basis to sanction it nor the intake channel to learn about it.

**(e) Retention.** Indefinite retention of a biometric sample whose purpose (one-time human review) completes in days is the clearest storage-limitation failure in the product — aggravated by the fact that it **survives account deletion**.

**(f) Law-enforcement requests.** Because room audio is not recorded, Cocorra **cannot** produce room-audio content in response to a request — a genuinely protective property that should be documented and preserved deliberately, not by accident. What Cocorra *can* produce: identity data, device IDs, message content, reports, participation records, hashed IPs, and **voice verification samples**. Part 9 addresses the retention framework.

## 5.3 Audio-specific recommended actions

| # | Action | Type |
|---|---|---|
| A1 | Delete every voice sample once verification is decided; retain at most a decision flag + timestamp | **Egyptian legal requirement** (minimisation/storage limitation) |
| A2 | Delete the voice file (and profile photo) in `DeleteAccountAsync` | **Egyptian legal requirement** (erasure) |
| A3 | Remove public-URL exposure of voice samples; serve via short-lived pre-signed URLs, admin-authenticated only | **Egyptian legal requirement** (Art. 4 security) |
| A4 | Explicit written consent + PDPC sensitive-data licence before continuing to collect samples | **Egyptian legal requirement** |
| A5 | Consider replacing the voice sample entirely with a non-retained liveness check | **Best practice** — removes the highest-risk category from the estate |
| A6 | Prohibit unauthorised recording/redistribution in the Terms + Community Guidelines; add a report category for it | **Contractual / best practice** |
| A7 | Log and alert on any LiveKit egress configuration change | **Best practice** — protects the "not recorded" claim |
| A8 | Rotate the LiveKit API secret and TURN credential out of git | **Egyptian legal requirement** (Art. 4 security) |

---

# PART 6 — Women-Only Platform Analysis

## 6.1 The threshold finding: it is not implemented

Before any legal question arises, the factual position must be stated: **Cocorra does not currently restrict access by gender in any way.** There is no gender attribute on the user, none in the registration DTO, and no check in any authorisation policy. The only gate is admin review of a voice sample (`UserStatus.Pending → Active`), which is a **human, subjective, undocumented** judgement.

That has two consequences, and the second is the more serious:

1. **Any "women-only" claim in marketing or an app-store listing is currently unsupported by the implementation** → misleading-claim exposure under Consumer Protection Law 181/2018 (Part 28), and an App Store/Google Play safety-claim problem (Part 29).
2. **In practice, gender is being inferred from a voice recording by an administrator.** That is an undisclosed use of biometric-category data to make an eligibility determination about a person. It is unwritten, unappealable, has no accuracy standard, and produces no record of the basis for the decision. Whatever the answer to the discrimination question below, *this mechanism* is the part most likely to cause a concrete legal problem — a rejected applicant has no way to know why, and Cocorra has no defensible record.

## 6.2 Is restricting a private service to women lawful in Egypt?

`LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`

What can responsibly be said:

- **No prohibition was located.** Research did not identify any Egyptian statute of general application that forbids a private digital service from limiting its membership to women. Egypt has **no general anti-discrimination statute governing the provision of private services** comparable to, say, UK or EU equality legislation. Single-sex provision is commonplace and socially normalised in Egypt (women-only metro carriages, gyms, salons, banking sections).
- **The Constitution's equality provisions bind the State**, not private contracting parties, and the Constitution additionally commits the State to protecting women. A constitutional-equality challenge to a private women-only app is not an obvious route.
- **Therefore the balance of the (limited) available evidence is that a women-only membership model is not unlawful in itself.** I am explicitly *not* stating that as a legal conclusion — it is an absence-of-prohibition finding, which is weaker, and confirming it is a job for Egyptian counsel.

**What I did *not* find, and what counsel must therefore be asked:** whether any consumer-protection, licensing, advertising-standards or platform-specific instrument constrains eligibility criteria; and whether a *rejected male* applicant has any cause of action.

## 6.3 Identity and gender verification — the trap to avoid

| Question | Assessment |
|---|---|
| Is identity verification legally required? | **No requirement was located** for a social/audio app. **Do not assume one exists.** |
| Is gender verification legally permissible? | Probably, as a contractual eligibility condition — **but the *method* determines the legal load** |
| Is collecting identity documents necessary? | **No** |
| Does collecting ID documents create additional obligations? | **Yes, severely.** A national ID number is an enumerated identifier; ID documents commonly reveal religion (Egyptian national IDs carry religion) — **an enumerated sensitive category**. Collecting them would add a second sensitive-data licence requirement, a much larger breach-impact profile, and a far higher-value target |
| **Should Cocorra avoid collecting official identity documents?** | **Yes — strongly, and this is the clearest recommendation in this Part.** The compliance cost, breach exposure and licence burden vastly exceed the verification benefit |

**Recommended posture:** treat "women only" as a **contractual eligibility term**, enforced by (i) a self-declaration captured at registration with a recorded timestamp, (ii) the existing human voice review — but **documented, criteria-based, logged, and appealable**, and (iii) report-driven enforcement with a dedicated `Impersonation`/`IneligibleAccount` report category. Publish the eligibility rule in the Terms. **Do not** collect national IDs. **Do** disclose in the privacy policy that the voice sample is used to assess eligibility — because it is, and currently that is undisclosed.

## 6.4 Related risks

| Risk | Assessment | Action |
|---|---|---|
| Discrimination claim | Low, on available evidence; unquantified | Counsel confirmation; publish eligibility terms |
| **Marketing/claim exposure** | **HIGH** — claiming women-only while enforcing nothing | Either implement enforcement or soften the claim; do not ship the claim unbacked |
| Impersonation / men joining | **HIGH and unmitigated** | Report category + documented review + enforcement |
| Fake accounts | **MEDIUM** — email + voice sample are the only friction; no phone verification | Consider phone verification (adds a PD category — weigh it) |
| **Safety expectation gap** | **HIGH.** Users who believe a space is women-only disclose more. If enforcement is weaker than the promise, the resulting harm is both a safety failure and a misrepresentation | Align promise to reality before launch |
| Voice-based gender determination | **HIGH** — undisclosed sensitive-data use, no criteria, no appeal | Disclose, document criteria, log decisions, provide appeal |

---

# PART 7 — Age / Minors

## 7.1 Current state

| Question | Finding | Evidence |
|---|---|---|
| Current minimum age | **None** | `RegisterDto.cs:18-19` — `[Required] public int Age` with no `[Range]` |
| Technically enforced? | **No.** No validation, no gate, no rejection path | grep: no age comparison anywhere |
| Is DOB collected? | **No** — a plain integer | `ApplicationUser.cs:14` |
| Can a minor bypass the gate? | **There is no gate to bypass.** A user may enter any integer, and may enter a false one | — |
| Is parental consent relevant? | **Yes, and mandatory below 15** — see 7.2 | ER 816/2025 |
| Does voice interaction add risk? | **Yes, materially.** Live, unrecorded, unmoderated adult-to-minor voice contact is the highest-risk configuration in online child safety, because it leaves no artefact to review after the fact | `RoomHub`; no audio moderation |
| Private communication with minors possible? | **Yes** — DMs between accepted friends, plus in-room private messages | `ChatHub`; `RoomHub.SendRoomPrivateMessage` |
| Can minors host rooms? | **Yes** — any `Active` user can create a room | `RoomService.CreateRoomAsync` |
| Can adults contact minors? | **Yes** — friend request → DM; or in-room private message. **`RoomHub.SendRoomPrivateMessage` does not appear to require friendship** — worth a focused re-read before launch | `RoomHub.cs:1004-1022`; `FriendsController` |
| Are report/block protections sufficient? | **No.** No child-specific report category, no minor-account flag, no escalation path, no prioritisation of reports involving minors | `ReportCategory.cs` |

## 7.2 Applicable Egyptian law

**PDPL + ER 816/2025 — children's data is sensitive personal data.**
- Data relating to children is expressly an enumerated **sensitive** category ([DLA Piper](https://www.dlapiperdataprotection.com/?t=law&c=EG)).
- **Under 15:** explicit **written** consent of the **guardian** required prior to processing for any purpose; consent must be **time-limited** and **revocable**.
- **15 to 18:** consent may be given by **either the child or the guardian**, in writing.
- **Consent mechanisms must be approved by the Centre.**
- Where a child participates in a game, contest or similar activity, **no more data may be collected than necessary**, and that data **may not be used for profiling, tracking or behavioural monitoring**.
> Sources: [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt); [Al Tamimi](https://www.tamimi.com/law_update_articles/from-policy-to-practice-egypt-issues-executive-regulations-of-the-personal-data-protection-law/).

**Direct application to Cocorra:** Cocorra's analytics pipeline is *precisely* behavioural monitoring — `UserEvent` records session, room, mic and speaking-time behaviour keyed to a user. If any registered user is under 18, that processing sits squarely in the restricted zone, and below 15 there is no guardian consent to authorise any of it.

**Law 175/2018 (cybercrime)** — Art. 25/26 privacy and reputation offences apply to conduct against minors as against anyone; the platform is the venue in which such conduct would occur.

**Child Law No. 12 of 1996 (as amended, notably by Law 126 of 2008)** — Egypt's principal child-protection instrument; a child is a person under 18. It establishes protection duties and criminal provisions concerning exploitation and endangerment. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED` on the precise duties this imposes on a private online platform, and on whether any mandatory-reporting obligation attaches to suspected child exploitation discovered on the service. **This is a question to take to counsel with priority**, because the answer changes the moderation design.

**Note on the brief's caution:** the brief correctly says not to assume 18+ is mandatory. It is not. Egyptian data protection law contemplates users aged 15–18 giving their own consent. **18+ is therefore a business/risk choice, not a statutory floor** — but see below for why it is nonetheless the right choice here.

## 7.3 Recommended, legally defensible age policy

**Recommendation: 18+, enforced, with the reasoning stated.**

Not because the law compels it, but because every alternative requires machinery Cocorra does not have and would struggle to build well:

| Option | What it would require | Assessment |
|---|---|---|
| **18+** | DOB at registration; hard rejection under 18; the eligibility term in the ToS; deletion of any under-18 accounts already registered | **Recommended.** Removes the children's-data sensitive category from the estate entirely; removes the guardian-consent, no-profiling and Centre-approved-mechanism obligations; simplifies the PDPC licence application; aligns with app-store age-rating for unmoderated live audio + DMs |
| **15+ with self-consent** | Written consent capture from 15–17s; **no behavioural profiling of them** (so a second analytics path); child-safety policy; minor-flagged accounts; restricted adult-minor contact | Legally available, operationally heavy. **Not advisable at current maturity** |
| **Any age with guardian consent under 15** | Verifiable guardian consent via a **Centre-approved mechanism**; full child-safety regime | **Not advisable** |

Supporting measures for the 18+ route: collect **date of birth** rather than an integer (an age integer cannot be re-validated as time passes, and cannot support a defensible record of what was asserted when); record the assertion with a timestamp as part of the consent record; treat a report that a user is a minor as a **priority** category with immediate suspension pending review; and state the policy plainly in the Terms so it is contractually enforceable.

**Interim measure pending the full fix:** add a minimum-age validation to `RegisterDto` and audit the existing user table for `Age < 18`. This is a small change with a large risk reduction, and it is the single highest-value-per-effort item in this audit.

---

# PART 8 — User-Generated Content

## 8.1 UGC surface inventory

| Surface | Field / mechanism | Persisted? | Moderated? | Char limit |
|---|---|---|---|---|
| Room titles | `Room.RoomTitle` `[Required]` | Yes | **No pre-screen** | 100 |
| Room descriptions | `Room.Description` | Yes | **No** | 250 |
| Display names | `ApplicationUser.FirstName/LastName` | Yes | **No** | 50 each |
| Bio | `ApplicationUser.Bio` | Yes | **No** | **no `MaxLength`** — unbounded |
| Profile pictures | `ProfilePicturePath` (**required**) | Yes, MinIO | **No image screening** | 
| Room images | `Room.ImagePath` | Yes, MinIO | **No** | 
| Direct messages | `Message.Content` | Yes | **No** | 1000 |
| In-room private messages | via `SaveMessageAsync` — **same table** | Yes | **No** | 1000 |
| In-room group messages | `RoomHub.SendRoomGroupMessage` | **No — ephemeral** | **No** | SignalR 32 KB cap |
| Live audio | WebRTC | No | **No** | — |
| Links | inside any free-text field | Yes | **No URL filtering** | — |
| Reports | `Report.Description` `[Required]` + `ScreenshotPath` | Yes | Admin-reviewed | **no `MaxLength`** |
| Support tickets/chat | `SupportTicket.Message`, `SupportMessage.Content` | Yes | Admin-reviewed | unbounded |
| Moderation actions | `AdminReportAction` (warn / mute 24h / ban / reject) | As `UserEvent` only | — | — |

**Two structural observations.** First, **there is no proactive moderation anywhere** — no word filter, no image classifier, no link scanner, no rate limit specific to messaging. Moderation is entirely reactive and human. Second, **in-room "private" messages are persisted to the same `Messages` table as friend DMs** (`RoomHub.cs:1016`), which means a user's expectation about a fleeting in-room aside is wrong: it is durable, admin-reachable content. That mismatch should be disclosed.

**Unbounded text fields** (`Bio`, `Report.Description`, `SupportTicket.Message`, `SupportMessage.Content`) are both a storage/DoS concern and a data-minimisation concern.

## 8.2 Risk mapping under Egyptian law

For each risk: what the law reaches, and what Cocorra currently has.

| Risk | Egyptian legal hook | Cocorra's current position |
|---|---|---|
| Harassment | Law 175/2018 Art. 26 (IT-enabled harm to reputation/dignity); Penal Code sexual-harassment provisions (as amended by Law 141/2021, which strengthened penalties) | `ReportCategory.Harassment` exists; **no definition, no prohibition in terms, no evidence preservation** |
| Threats | Penal Code threat offences | No specific category; falls to `Other` |
| Defamation / insult | Penal Code defamation and insult (*sabb wa qadhf*) — a live, frequently used cause of action in Egypt, both criminal and civil | **No category; no notice-and-action process** |
| Sexual content | Law 175/2018 Art. 25 ("family principles and values"); Penal Code public-indecency provisions | `InappropriateContent` category only |
| Exploitation | Child Law 12/1996 (as amended); Penal Code; Law 175/2018 | **No child-specific category; no escalation; no preservation** |
| Blackmail / sextortion | Penal Code extortion; Law 175/2018 | **No category** |
| Hate speech | Penal Code contempt-of-religion (Art. 98(f)) and incitement provisions | **No category** |
| Illegal activity | general criminal law | **No category** |
| Impersonation | Law 175/2018 (fraudulent use of accounts/identity) | `FakeIdentity` category exists — **the one well-matched category** |
| Fraud / scams | Penal Code fraud; Law 175/2018 | **No category** |
| Privacy violation / doxxing | **PDPL Art. 2** (no disclosure without consent) + **Law 175/2018 Art. 25** | **No category** |
| Unauthorised recording & redistribution | Law 175/2018 Arts. 25–26 | **No category, no prohibition** — see Part 5(d) |

**The `ReportCategory` enum has five values** (`InappropriateContent`, `Harassment`, `Spam`, `FakeIdentity`, `Other`) against **twelve** distinct legal risk classes. Everything unmapped collapses into `Other` — which means it cannot be prioritised, cannot be triaged by severity, cannot be routed to the right response (police referral vs. warning), and cannot be reported on. For a platform in a jurisdiction that actively prosecutes online speech, **a report taxonomy that cannot distinguish "spam" from "a child is being groomed" is a design defect with legal consequences.**

## 8.3 The Article 25 problem — specific to Egypt

This deserves separate statement because it is the risk most likely to be underestimated by a team reasoning from Western platform norms.

**Law 175/2018 Art. 25** penalises content violating "the family principles and values upheld by Egyptian society", with a minimum of six months' imprisonment and/or a fine. The provision is deliberately open-textured. It has been applied extensively and controversially: the most prominent prosecutions were of young women who became prominent on TikTok (Haneen Hossam and Mawada Eladham, arrested 2020), with more than a dozen women facing similar charges, pretrial detention and custodial sentences; enforcement has since broadened, and complaints are frequently initiated by **private individuals** against one another. Reported figures include the removal of over 2.9 million videos deemed to violate "public morals".

> Sources: [TIMEP](https://timep.org/2020/08/13/egypts-tiktok-crackdown-and-family-values/); [SMEX / Columbia GFoE](https://globalfreedomofexpression.columbia.edu/publications/the-tiktok-case-a-new-platform-to-oppress-women-in-egypt/); [EIPR](https://eipr.org/en/press/2025/08/crackdown-content-creators-mix-security-repression-class-discrimination-and-%E2%80%9Cmoral); [The Conversation](https://theconversation.com/tiktok-in-egypt-where-rich-and-poor-meet-and-the-state-watches-everything-253278).

**Why this lands directly on Cocorra.** Cocorra is (a) a platform whose intended user base is women, (b) built on live voice — the least reviewable medium, (c) with room categories inviting intimate disclosure (`Relationships`, `MentalHealth`), (d) in Arabic. That is close to the exact profile that has attracted enforcement attention. The realistic risk is **not** that Cocorra is prosecuted as a platform in the first instance; it is that **a host or user is prosecuted for something said in a Cocorra room**, and Cocorra is then compelled to produce data, is publicly associated with the case, and faces demands it has no process to handle.

**What follows practically:**
1. Cocorra will receive law-enforcement demands. It needs a documented procedure **before** the first one arrives (Part 25/34).
2. Cocorra should be able to state truthfully and verifiably that **it cannot produce room audio**. That is protective for users and for Cocorra. It is currently true; keep it true, and document it.
3. Community Guidelines should be drafted with awareness of Art. 25 — not to endorse it, but so that users are warned of the actual legal environment they are speaking in. **This is a duty-of-candour point:** a platform that markets itself as a safe space for Egyptian women to discuss relationships and mental health, without warning them that such speech has attracted prosecution in Egypt, is under-informing them about a material risk.
4. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED` — on platform-level exposure under Arts. 25/26 for hosting third-party content, and on whether any takedown/notification duty attaches on notice.

## 8.4 Determinations required

| Question | Recommendation |
|---|---|
| What should Cocorra prohibit? | All twelve categories in 8.2, plus: unauthorised recording/redistribution; sharing others' private information; sexual content; contact directed at minors; commercial solicitation; ban evasion. To be set out in Community Guidelines **incorporated by reference into the Terms** (Part 21) |
| What must be reported to authorities? | `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`. Specifically: is there a mandatory-reporting duty for suspected child exploitation, credible threats to life, or terrorism-related content? Do **not** guess — the answer determines whether a moderator's silence is itself an offence |
| What should be removed | Content in breach of the Guidelines; accounts of ineligible or banned users; content subject to a valid legal order |
| **What evidence should be preserved** | On a report: the reported content, reporter and reported identities, room and participation context, timestamps, moderator decision and reasoning. **Preserve on a legal-hold flag that survives account deletion** — currently deletion destroys or anonymises it (Part 23). Balance against PDPL minimisation: preserve *scoped to the report*, not wholesale |
| How moderation decisions should be logged | **A dedicated, durable, append-only `ModerationAction` table** — not `UserEvent`, which drops on overflow and self-deletes at 180 days (Part 3.3(24)) |
| How appeals should work | **No appeal mechanism exists.** A banned user is locked out to `DateTimeOffset.MaxValue` and cannot even read their own notifications (`README.md:217-219` acknowledges this). Email is the only reachable channel. Needs: written notice of reason, an appeal route that works while banned, a decision record, and a defined response time |
| How law-enforcement requests should be handled | Documented procedure: single intake point, validity/authority verification, legal review, minimum-necessary disclosure, requester identity and scope logged, user notification where lawful. **None of this exists** |

---

# PART 9 — Cybercrime / IT Law (Law 175 of 2018)

## 9.1 The central question: is Cocorra a "service provider"?

`LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED — HIGHEST PRIORITY QUESTION IN THIS AUDIT AFTER THE PDPL LICENCE`

**Law 175/2018 Art. 2** obliges service providers to retain and store data enabling identification of users, together with traffic data and other data, **for 180 consecutive days**. **Art. 33** penalises failure with a fine of **EGP 5,000,000 to 20,000,000**, doubled on repetition, with possible revocation of the licence to operate in Egypt.

> Sources: [Library of Congress](https://www.loc.gov/item/global-legal-monitor/2018-10-05/egypt-president-ratifies-anti-cybercrime-law/); [Mada Masr guide](https://www.madamasr.com/en/2018/08/21/feature/politics/how-you-will-be-affected-by-the-new-cybercrime-law-a-guide/); [TIMEP](https://timep.org/2018/12/19/cybercrime-law-brief/).

**Why the answer is not obvious.** Commentary describes Art. 2 as directed at "telecommunications companies" and at "telecom, web hosting and cloud computing companies". Cocorra is none of those in the ordinary sense. But the statutory definition of "service provider" in Art. 1 is the operative text, and I was **unable to obtain a reliable English rendering of it** — the available translations of Law 175/2018 are image-only scanned PDFs from which text could not be extracted. **I will not paraphrase a definition I could not read.**

**Why this matters enormously, in both directions:**

- **If Cocorra *is* a service provider:** it faces a mandatory 180-day retention duty over user-identification and traffic data, backed by an **EGP 5–20 million** fine. It would then be *under-retaining* — it stores no traffic data as such, and its most log-like artefact (`UserEvent`) is pruned at exactly 180 days by a service whose alignment with the statute is coincidental. It would also need to reconcile that duty with PDPL minimisation, and would likely need to register/licence accordingly.
- **If Cocorra is *not* a service provider:** then building 180-day identification/traffic logging would be **gratuitous over-collection** — creating a large, sensitive, offshore dataset with no legal mandate, which is itself a PDPL exposure and directly contrary to the brief's instruction not to recommend excessive logging.

**The two readings point to opposite engineering actions.** This is therefore the clearest example in this audit of a question that must be answered by counsel **before** anything is built. Do not implement retention logging speculatively; do not delete logs on the assumption the duty does not apply.

## 9.2 Architecture mapped to Law 175/2018 offence and duty categories

| Concept | Cocorra's position | Evidence | Assessment |
|---|---|---|---|
| Unauthorised access (Arts. 14–17) | JWT bearer + Identity; default policy requires `VerificationStatus == Active`; lockout re-checked **every request** | `Program.cs:360-373, 385-396` | Sound design — **undermined entirely by the committed signing key** (Part 24) |
| Account takeover | 8-char complexity, lockout after 5 attempts/15 min, refresh rotation | `Program.cs:306-317` | Reasonable. **No MFA. No login-notification. Refresh token stored in plaintext** |
| Impersonation | `FakeIdentity` report category; voice review | `ReportCategory.cs` | Weak but present |
| Unauthorised use of accounts | Device registry gives a genuine detection signal | `BlockedDevices` | Signal exists; **nothing consumes it** for anomaly detection |
| Device blocking | `DeviceBlockingMiddleware` on `IsBlocked=true` | `DeviceBlockingMiddleware.cs` | Works; defeated by client omitting `X-Device-Id`, which is treated as "nothing to register", never an error (`DeviceHeaderExtensions.cs:23-27`) |
| IP logging | **Salted hash only**, 180 days | `UserEvent.IpHash`; `Program.cs:231-246` | **Good privacy engineering.** But an irreversible hash **cannot satisfy an obligation to identify a user by IP** — if Art. 2 applies, hashing defeats compliance. The two regimes pull in opposite directions and counsel must resolve which governs |
| Authentication logs | **None as such.** No login-success/failure table; `BlockedDevices.LastSeenAt` is the closest artefact | grep | **Gap under either reading** — no ability to investigate an account compromise |
| Access logs | Docker json-file, 10 MB × 3 | `docker-compose.yml:51-55` | **Effectively no retention.** On a busy day these rotate away in hours |
| Security logs | None distinct | — | Gap |
| Evidence preservation | **None.** No legal-hold concept anywhere | — | Gap; conflicts with deletion (Part 23) |
| User reports | Present | `Report` | Present but taxonomy inadequate (Part 8) |
| Illegal content | No detection, no takedown workflow, no notice-and-action | — | Gap |
| Disclosure of data | No documented procedure; **no restriction on which staff can query the DB** | — | Gap. Note **Art. 25** criminalises unlawful disclosure — this cuts against Cocorra as well as against users |
| Law-enforcement requests | No procedure, no intake point, no log | — | Gap |
| Data retention | Only `RawEventRetentionDays: 180` | `appsettings.json:86` | Single implemented period |
| System security | See Part 24 | | **Multiple critical failures** |

## 9.3 Logging recommendation — balanced, as the brief requires

The brief rightly warns against recommending excessive logging. Applying that discipline, and **assuming Art. 2 does not apply** (to be confirmed):

**Should be added — justified by security/accountability, proportionate, and independently defensible under PDPL Art. 4:**
1. **Authentication event log** — user ID, outcome, timestamp, device ID, hashed IP. No raw IP. Retention **90 days**, or whatever counsel advises. Justification: detecting and investigating account compromise is a security duty, not surveillance.
2. **Durable moderation/admin action log** — append-only, separate from `UserEvent`, **not** subject to the 180-day prune, recording actor, subject, action, reason, timestamp. Justification: ER require registers of decisions and of data-subject requests, inspectable by the regulator.
3. **Sensitive-data access log** — every access to a voice sample or report, by whom. Justification: this is the highest-risk data in the estate and currently nobody knows who has looked at it.
4. **Legal-hold flag** on user/report records, blocking deletion while set. Justification: reconciles erasure with evidence preservation instead of letting them collide silently.
5. **Breach register** — required by the ER (Part 25).

**Should NOT be added absent a confirmed legal duty:** raw IP retention; content logging of messages or audio; full-request logging; browsing/traffic data; long-retention behavioural logs. Each would enlarge the offshore sensitive dataset without a mandate.

**Should be removed:** the `[LIVEKIT-TOKEN-AUDIT]` identity logging, which is explicitly self-labelled temporary (`LiveKitService.cs:56-61`).

---

# PART 10 — Telecommunications / NTRA

## 10.1 The licensing framework

Under **Telecommunication Regulation Law No. 10 of 2003**, no person may establish or operate a telecommunications network, provide a telecommunications service to third parties, or transmit international calls, **without a licence from the NTRA**. "Telecommunication Service" is defined around services "provided for remuneration which consist wholly or partly in the transmission and routing of signals on telecommunication networks".

> Sources: [NTRA — Law 10/2003](https://www.tra.gov.eg/en/rules/law-no-10-of-2003/); [Lexology — telecoms regulation in Egypt](https://www.lexology.com/library/detail.aspx?g=85c424f1-84bb-4288-8d48-df69c913cbc9); [Privacy International — State of Privacy Egypt](https://privacyinternational.org/state-privacy/1001/state-privacy-egypt).

## 10.2 Assessment

**`LIKELY NOT APPLICABLE` — with one genuine qualification and one distinct, unrelated concern.**

**Reasoning for "likely not applicable":**
1. Cocorra does not operate a telecommunications network in the Law 10/2003 sense. It runs application software over the public internet, carried by licensed operators.
2. Cocorra provides **no** interconnection with the PSTN, **no** numbering, **no** international call transmission, **no** carriage of third-party traffic. Audio exists only between authenticated Cocorra users inside the application.
3. Cocorra is currently **free** — the "provided for remuneration" element of the service definition is not met today. **Note this changes on monetization** and should be re-tested then.
4. Internationally, closed-user-group in-app voice is generally treated as an application/OTT service outside carrier licensing.

**The qualification — `POTENTIALLY APPLICABLE — SPECIALIST REVIEW REQUIRED`:**
Cocorra does not merely embed a third party's audio SDK. It **operates its own media infrastructure**: a self-hosted LiveKit SFU **and its own TURN relay server** with static credentials, on ports 3478/5349, terminating and relaying real-time voice for all users (`livekit.yaml:84-101`). Every packet transits Cocorra's server. That is materially closer to "operating infrastructure that transmits and routes signals" than a client-side SDK would be, and it is a distinction an Egyptian telecoms specialist should be asked about explicitly. **The self-hosting decision — excellent for data protection — is the fact that weakens the "we are just an app" position.** Had Cocorra used LiveKit Cloud, the telecom argument would be cleaner and the PDPL position worse.

**Does third-party infrastructure change the analysis?** Yes, in both directions, and the trade-off should be a conscious one:

| Model | Telecom exposure | PDPL exposure |
|---|---|---|
| Self-hosted LiveKit + own TURN (**current**) | **Higher** — Cocorra operates the relay | **Lower** — no third-party processor sees media |
| LiveKit Cloud / managed SFU | **Lower** — Cocorra is plainly an application | **Higher** — a further cross-border processor for voice content |

**The distinct, unrelated concern — Article 64:**
Art. 64 of Law 10/2003 requires operators to provide the technical means for the armed forces and national security agencies to exercise their powers, and **prohibits the use of telecommunications encryption equipment without prior written consent from the NTRA, the Armed Forces and national security entities**.

WebRTC **mandates** encryption — DTLS-SRTP for media is not optional in the protocol — and Cocorra additionally runs TURNS on 5349 and HTTPS throughout. In practice Art. 64 is not enforced against ordinary TLS or consumer applications, and reading it to outlaw HTTPS in Egypt would be absurd. But the text is broad, it is directed at operators, and whether Cocorra's operation of an encrypting media relay engages it is not something I can resolve from public sources.

`LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED` on: (i) whether operating a self-hosted SFU/TURN for a closed user group requires any NTRA authorisation, class licence, or notification; (ii) whether Art. 64 has any practical application to Cocorra's encrypted media; (iii) whether the position changes on monetization; and (iv) whether hosting that media relay **outside Egypt** has any NTRA implication.

**What I explicitly do not claim:** that a telecom licence is required. No legal basis was found for that assertion.

---

# PART 11 — Consumer Protection

## 11.1 Framework

**Consumer Protection Law No. 181 of 2018**, with its Executive Regulations, administered by the **Consumer Protection Agency (CPA)**. It covers e-commerce and distance contracting expressly.

Key provisions identified:
- **Pre-contract disclosure for distance/remote contracting:** the supplier must give the consumer clear, explicit information including **supplier identity and contact details, commercial registration number, tax card**, and professional information, together with the essential characteristics of the product/service, how to use it, and any risks.
- **Right of withdrawal:** the consumer contracting at a distance may withdraw **within 14 days** of receipt of goods.
- **Refund:** amounts must be refunded **within 7 days** — from return of the product for goods, or **from the date of contracting for services**.
- Exceptions exist (e.g. custom-made or perishable items).

> Sources: [WIPO Lex — Law 181/2018](https://www.wipo.int/wipolex/en/legislation/details/19866); [Law text (PDF)](https://www.africa-laws.org/Egypt/Consumer%20Law/Law%20No.%20181%20of%202018%20on%20Consumer%20Protection.pdf); [Shalakany — CPL Executive Regulations](https://shalakany.com/new-consumer-protection-law-executive-regulations/); [UNESCWA — Egypt consumer protection](https://www.unescwa.org/sites/default/files/inline-files/ABLF-2023-consumer-CP-Egypt-english.pdf); [Lexology — e-commerce in Egypt](https://www.lexology.com/library/detail.aspx?g=0f6bc608-2b26-4d33-a646-ada72e0f980b).

## 11.2 Current-state audit

| Item | Cocorra now | Requirement | Status |
|---|---|---|---|
| Terms of Service | **None exist** | Contract terms must be available and clear | **CRITICAL GAP** |
| Supplier identity disclosure | **None** — only a Gmail address | Identity, contact, **commercial registration, tax card** | **GAP** (blocked on Part 2) |
| Pricing | N/A — free | Must be clear and inclusive if charged | N/A |
| Subscriptions / free trials / auto-renewal | None | Terms, renewal notice, cancellation | N/A |
| Refunds / cancellations | None | 14-day withdrawal; 7-day refund | N/A |
| Promotional offers | None | No misleading offers | N/A |
| **Misleading claims** | **LIVE RISK** — any "women-only", "safe", "private", "not recorded" claim is currently unsupported by implementation | Claims must not mislead | **HIGH** — Part 28 |
| Hidden fees | None | — | N/A |
| Payment disputes | N/A | — | N/A |
| **Account termination** | Permanent lockout to `DateTimeOffset.MaxValue`, **no notice of reason, no appeal, banned user cannot read their own notifications** | Fair, transparent contractual terms | **GAP** — likely an unfair term absent a stated basis |
| Service availability | No SLA, no status page; `README.md:12` notes Swagger is enabled in production | Availability representations must be accurate | **INFORMATIONAL** while free |
| Liability disclaimers | **None** — no terms at all | Permitted but limited; total exclusions vulnerable | **GAP** |
| **Customer support** | Exists and is reasonably built — tickets (anonymous permitted), real-time chat, admin claim/reply/close | A complaint channel is required | **PRESENT — a genuine strength** |
| Complaint handling | No documented procedure, no SLA, no escalation, no CPA-referral notice | Documented complaint handling | **GAP** |

## 11.3 What changes on monetization

Consumer protection moves from mostly-latent to **primary regulatory exposure**. Required before charging any Egyptian consumer:

1. **Pre-contract disclosure page** carrying legal entity name, address, commercial registration number, tax card number, contact channel — none of which exist today (Part 2).
2. **Subscription Terms** as a distinct document: price inclusive of VAT, billing period, renewal date, **auto-renewal disclosed prominently**, cancellation method, effect of cancellation mid-period.
3. **Refund Policy** reflecting the 14-day withdrawal right and the **7-day refund deadline running from the date of contracting for services** — note this is stricter than many teams assume, and it is a services deadline, not a goods deadline.
4. **Digital-services withdrawal analysis** — whether and how the withdrawal right applies to a partly-consumed digital service, and whether an express waiver on commencement of performance is effective in Egypt. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`.
5. **Cancellation and refund implemented in code and in support workflow**, with records.
6. **Complaint procedure** with response SLA and CPA referral information.
7. **No dark patterns** — no pre-ticked renewal, no obscured cancellation.
8. **VAT-inclusive display** (Part 14).

---

# PART 12 — Electronic Contracts

## 12.1 Framework

**Law No. 15 of 2004 on E-Signature** and the establishment of ITIDA, with Executive Regulations under **MCIT Decree No. 109 of 2005**. The core principles identified:
- Contracts **may not be denied enforceability merely because they were concluded electronically**, provided the law's technical requirements are satisfied.
- Electronic signatures **certified by ITIDA-licensed providers (Qualified Electronic Signatures)** are **automatically admissible** in evidence (Arts. 14–15).
- Otherwise, admissibility of electronic evidence is assessed by the court under the **Law of Civil and Commercial Procedures**, as complemented by Arts. 15 and 18 of the E-Signature Law.

> Sources: [WIPO Lex — Decree 109/2005](https://www.wipo.int/wipolex/en/legislation/details/13700); [ITIDA — Executive Regulations (PDF)](https://www.itida.gov.eg/English/Documents/4.pdf); [Mondaq — Electronic signatures in Egypt](https://www.mondaq.com/contracts-and-commercial-law/1737494/electronic-signatures-in-egypt-%7C-legal-validity-under-law-15-of-2004); [DocuSign — Egypt](https://www.docusign.com/products/electronic-signature/legality/egypt).

**The practical takeaway for a consumer app:** a QES is **not** required or proportionate for accepting terms of service. What matters is that Cocorra can **evidence** what was presented, what was accepted, by whom, and when — because absent a QES, the court weighs the reliability of the evidence adduced. Weak records mean a weak contract.

## 12.2 Audit

| Element | Cocorra now | Assessment |
|---|---|---|
| Terms of Service | **Does not exist** | No contract to form |
| Privacy Policy | **Does not exist** | No notice given |
| Community rules | **Do not exist** | Nothing to enforce |
| Subscription terms | N/A | — |
| **Acceptance mechanism** | **None.** `RegisterDto` has no acceptance field; registration completes without any assent | **CRITICAL** |
| **Acceptance records** | **None** | Cannot prove assent |
| **Versioning** | **None** | Cannot prove *which* terms |
| **Timestamps** | **None** | Cannot prove *when* |
| Identity/account association | Account identity is solid (unique email, Identity framework) — **but nothing to associate it with** | Foundation fine |
| Policy updates | No mechanism | Cannot re-paper |
| User notification of changes | Notification infrastructure **exists and is good** (`Notification` rows + FCM, persist-then-push) — **unused for this purpose** | Reusable |
| Proof of acceptance | **None** | **CRITICAL** |

## 12.3 Clickwrap vs browsewrap

**Cocorra currently has neither.** It has *no*-wrap: nothing is presented and nothing is accepted. Absent a QES, evidential weight is at the court's assessment, so the strongest available position is a **clickwrap** with a durable record:

- An **unticked, mandatory** checkbox at registration: *"I have read and accept the Terms of Service and Privacy Policy"*, with both linked and readable **before** submission.
- **Separate, optional, unticked** consents for analytics and push — never bundled with the contractual acceptance. The ER's prohibition on secondary use makes this separation a legal requirement, not just good form.
- **Server-side rejection of registration** without the mandatory acceptance — a client-only checkbox proves nothing.
- Persist: user ID, document type, **version identifier**, **hash of the exact published text**, UTC timestamp, method, and app/build version.
- Immutable, versioned publication of each policy text so the hash can be re-derived years later.
- On material change: notify via the existing `Notification` + FCM path, require re-acceptance for material changes, and **retain the prior acceptance record** rather than overwriting it.
- Retain acceptance records for the limitation period plus a margin (**take the period from counsel**), and mark them **legal-hold** so account deletion does not destroy the proof that terms were accepted.

> Note the tension to raise with counsel: **erasure vs. evidence.** A deleted user's acceptance record is the only proof Cocorra had a contract with them. Retaining a minimal, pseudonymised acceptance record after deletion is likely justifiable, but the basis should be confirmed, not assumed.

---

# PART 13 — Payments / E-Commerce

## 13.1 Current state: no payment functionality exists

Verified by inspection, not assumption:
- **No payment provider** — no Paymob, PayTabs, Fawry, Stripe, Kashier or any other integration. The full NuGet dependency set across all four projects is: FirebaseAdmin, MailKit/MimeKit, MediatR, EF Core (+SqlServer/Design/Tools), AWSSDK.S3, Livekit.Server.Sdk.Dotnet, Swashbuckle, JwtBearer, Identity.EntityFrameworkCore, and test-only packages. **No payment SDK.**
- **No card handling** — no card, PAN, CVV, expiry, token or cardholder field in any model or DTO.
- **No subscription logic** — no plan, entitlement, price, billing-period or renewal concept.
- **No invoices, refunds, chargebacks, VAT calculation, or payment data storage.**

**This is the single best legal position in the audit.** Cocorra currently holds **zero** payment data and therefore carries no PCI-DSS scope, no payment-data breach exposure, and no financial-services regulatory contact. It should be preserved deliberately.

## 13.2 What Cocorra MUST NOT store — if and when payments are added

Stated now, because these decisions get made under delivery pressure:

| Never store | Why |
|---|---|
| Full card number (PAN) | PCI-DSS scope explosion; PDPL financial data is **sensitive personal data** requiring a PDPC permit |
| CVV/CVC/CID | **Prohibited from storage post-authorisation under PCI-DSS in all circumstances** |
| Magnetic stripe / chip track data | Prohibited |
| PIN or PIN block | Prohibited |
| Full card number in logs, `PropertiesJson`, support tickets, screenshots, or error messages | Same, and `Report.ScreenshotPath`/`SupportTicket.ScreenshotPath` are an obvious accidental-capture route |
| Bank account / IBAN for user payouts, unencrypted | Financial data = sensitive under PDPL |

**Safe to store:** provider-issued opaque token/customer reference; last four digits; card brand; expiry month/year (for renewal UX); transaction ID; amount; currency; status; timestamps. **Also note:** financial data is an enumerated **sensitive** category under PDPL Art. 1 — so even last-four/brand data should be treated as requiring the sensitive-data licence conditions, and the cross-border position re-tested (a payment provider is another offshore recipient).

## 13.3 `FUTURE MONETIZATION LEGAL CHECKLIST`

Ordered as a gate sequence. Items marked **[BLOCKER]** must complete before the first EGP is charged.

**Corporate & tax**
1. **[BLOCKER]** Registered Egyptian legal entity with commercial registration and tax card (Part 2) — a prerequisite for both a payment-provider contract and the CPL disclosure duty.
2. **[BLOCKER]** Tax registration; determine VAT registration obligation and timing (Part 14).
3. Confirm the correct VAT rate and treatment for the specific service (14% standard vs 10% professional/consultancy — the classification matters and is not obvious for coaching-adjacent services).
4. Confirm corporate income tax position and, if any founder/entity is non-resident, withholding and permanent-establishment analysis.

**Payment infrastructure**
5. **[BLOCKER]** Select a provider licensed/permitted to acquire in Egypt (Paymob, Fawry, PayTabs, Kashier are the common domestic routes). **Verify the provider's own regulatory status with the Central Bank of Egypt** — do not take it on trust from a sales page.
6. **[BLOCKER]** Hosted fields / redirect / provider SDK only — **never** collect card data on Cocorra's own forms or servers.
7. **[BLOCKER]** Execute the provider's data-processing terms; assess the cross-border position if the provider processes outside Egypt (Part 16).
8. Confirm the settlement account is in the registered entity's name.
9. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`: whether facilitating **host/coach payouts** turns Cocorra into a payment intermediary or agent requiring CBE authorisation under the payment-services framework. **This is the question most likely to be missed** and it changes the whole architecture. Ask it *before* designing a revenue-share.

**Consumer protection (Part 11)**
10. **[BLOCKER]** Pre-contract disclosure with entity identity, commercial registration number, tax card.
11. **[BLOCKER]** Subscription Terms with VAT-inclusive price, billing period, auto-renewal prominently disclosed, cancellation route.
12. **[BLOCKER]** Refund Policy: 14-day distance withdrawal; **7-day refund from date of contracting for services**.
13. **[BLOCKER]** Working in-product cancellation — not email-only.
14. Complaint procedure with SLA and CPA referral notice.
15. No pre-ticked renewal; no obscured cancellation.

**Data protection**
16. **[BLOCKER]** Update the Privacy Policy: payment data categories, provider identity, retention, cross-border transfer.
17. **[BLOCKER]** Financial data is sensitive — confirm whether the PDPC licence must be varied.
18. Retention schedule for transaction records — reconcile the commercial/tax retention period (**take the statutory period from an Egyptian accountant**; do not assume) against PDPL minimisation, and mark those records legal-hold so account deletion does not destroy them.

**Platform (Part 29)**
19. **[BLOCKER]** If billing occurs inside an iOS/Android app for digital content, **Apple and Google will require their own in-app purchase**, taking 15–30%, and will reject external payment links. This is a **platform** requirement, not Egyptian law, and it materially changes unit economics and the tax analysis (Apple/Google may act as merchant of record). **Resolve this before choosing a payment provider**, or the provider work is wasted.

---

# PART 14 — Tax / VAT

> **This section identifies questions, not answers.** Definitive tax positions require facts not present in the repository (entity residence, shareholding, revenue, contracts). Nothing here is tax advice.

## 14.1 Framework identified

| Regime | Position |
|---|---|
| **Corporate income tax** | Applies to a registered Egyptian entity on its profits. Rate, exemptions, and any small-business/SME regime must be confirmed by an Egyptian tax accountant. **No revenue currently exists.** |
| **VAT — Law 67 of 2016** | Standard rate **14%**; **10%** for professional and consultancy services. |
| **VAT on digital / remote services** | The **Egyptian Tax Authority** has issued VAT guidelines for **non-resident** providers of digital and other remote services supplied to customers in Egypt via websites, social-media stores and applications, with a **simplified online registration** regime requiring no physical presence. |
| **Simplified-regime threshold** | Registration where turnover reaches **EGP 500,000 in any 12 months** — **but** where the service is a **professional/consultancy service subject to the reduced rate, registration is required regardless of turnover**. |
| **Returns** | **Monthly** under the simplified regime, due by the end of the following month. |

> Sources: [ETA — VAT guidelines for digital services](https://eta.gov.eg/en/content/egyptian-tax-authority-eta-has-recently-published-value-added-tax-vat-guidelines-digital); [ETA — simplified VAT for non-resident vendors and EDPs](https://eta.gov.eg/en/content/digital-service-gd); [EY](https://www.ey.com/en_gl/technical/tax-alerts/egypt-introduces-vat-guidelines-for-nonresident-providers-of-rem); [Andersen Egypt](https://eg.andersen.com/tax-digital-services-in-egypt/); [Avalara](https://www.avalara.com/us/en/vatlive/country-guides/africa-and-middle-east/egypt-vat/egyptian-vat-digital-services.html).

## 14.2 Model-specific analysis

| Element | Current | On monetization — the question to ask |
|---|---|---|
| Corporate income tax | No entity identified, no revenue | Which entity is taxable; residence; whether any SME/startup regime applies |
| VAT registration | Not applicable | Is Cocorra resident or non-resident for these purposes? If an Egyptian entity, the ordinary VAT regime applies, not the simplified non-resident one |
| **Rate classification** | N/A | **The key open question: is a coach-led audio session a "digital service" at 14%, or a "professional/consultancy service" at 10%?** This is not academic — if it is professional/consultancy, **the EGP 500,000 threshold does not apply and registration is required from the first sale.** A "coaching" platform is squarely in the ambiguous zone |
| Subscriptions | None | VAT on each period; VAT-inclusive price display (CPL) |
| Digital services | Core model | Place-of-supply rules for Egyptian consumers |
| Advertising | None | Ad revenue is a distinct VAT and income analysis |
| Commissions / platform fees | None | Whether Cocorra's fee is a separate taxable supply from the host's supply |
| **Creator / host payments** | None | **Highest-complexity area.** Are hosts employees, contractors, or independent suppliers? Withholding tax on payments? Are hosts themselves VAT-registrable? Is Cocorra an agent or principal? Interacts directly with Part 27 |
| Refunds | None | Credit notes and VAT adjustment mechanics |
| Electronic invoicing | None | **Egypt operates mandatory e-invoicing/e-receipt systems.** Confirm applicability and integration burden — this is an engineering workload, not just a filing one |

## 14.3 Must be reviewed by an Egyptian tax accountant

1. Corporate structure and residence; whether any founder is non-resident.
2. **Rate classification of the core service (14% vs 10%)** and the threshold consequence.
3. Whether the ordinary or simplified non-resident VAT regime applies.
4. Host/coach payout characterisation and withholding obligations.
5. **Mandatory e-invoicing / e-receipt applicability** and system integration.
6. **Statutory retention period for accounting records** — needed to complete the Part 17 retention schedule; currently unknown and cannot be guessed.
7. Whether German hosting creates any Egyptian or German tax nexus.
8. Treatment of App Store / Google Play as merchant of record, if IAP is used.

---

# PART 15 — Third-Party Services

## 15.1 Inventory — from code, not assumption

| Provider | Data Shared | Purpose | Location | International Transfer | Contract/DPA | Risk |
|---|---|---|---|---|---|---|
| **Hostinger** (VPS `152.239.115.176`) | **Everything** — SQL Server DB, MinIO objects (voice samples, photos), API, LiveKit media, TURN relay | Hosting all compute and storage | **Frankfurt, Germany** (AS47583) — *geolocation evidence; verify contractually* | **YES — the entire data estate** | **NOT FOUND** | **CRITICAL** |
| **Google — Firebase Cloud Messaging** | FCM device token; notification title/body **including message text** (`MessagePreview`) | Push notifications | Google global (US/EU) | **YES** | **NOT FOUND** | **HIGH** |
| **Google — Gmail SMTP** (`smtp.gmail.com`, `cocorra02@gmail.com`) | Recipient email; OTP codes; notification content | Transactional email | Google global | **YES** | **NOT FOUND** — and a **consumer Gmail account with an app password is not a business email service**; check it against Google's ToS for automated transactional sending | **HIGH** |
| **Google STUN** (`stun.l.google.com`, `stun1.l.google.com`) | Client IP addresses (**raw**, from users' devices) | NAT traversal | Google global | **YES** | N/A | **MEDIUM** |
| **Cloudflare STUN** (`stun.cloudflare.com:3478`) | Client IP addresses (raw) | NAT traversal | Cloudflare global | **YES** | N/A | **MEDIUM** |
| **LiveKit (self-hosted software)** | Room/participant identifiers, media in transit | Audio SFU | **On the Frankfurt VPS** | Transfer is to Germany, **not** to LiveKit Inc. | Apache-2.0 licence; **no processor relationship** | Included in Hostinger row |
| **TURN (self-hosted, in LiveKit)** | **All media relayed**, client IPs | NAT traversal fallback | Frankfurt VPS | Same | Same | Included above |
| **MinIO (self-hosted)** | Voice samples, profile photos, room images, report/ticket screenshots | Object storage | Frankfurt VPS, `:9000` **over plain HTTP** | Same | Same | **HIGH** (transport + likely public bucket) |
| **SQL Server (self-hosted)** | All relational personal data | Database | Frankfurt VPS, port 1433, `sa` account | Same | Same | **CRITICAL** (credentials in git) |
| **GitHub / GitHub Actions** | **Source code containing live production secrets**; deploy SSH key; `APPSETTINGS_JSON` | CI/CD | GitHub (US) | **YES — secrets, not user data** | GitHub ToS | **CRITICAL** |
| **Redis** | — | — | — | — | — | **NOT PRESENT** — the brief lists it, but no Redis client, connection string or container exists. `IMemoryCache` is used instead (`Program.cs:216`) |
| **Caddy** | — | — | — | — | — | **NOT FOUND in the repository.** TLS termination for `api.cocorraapp.com` / `storage.cocorraapp.com` / `live.cocorraapp.com` must be configured **outside** version control. **Unaudited infrastructure** — obtain and review it |
| **DNS / CDN** | — | — | — | — | — | **Not evidenced in the repo.** `cocorraapp.com` DNS provider unknown; no CDN configured |
| **Analytics SDK (third-party)** | — | — | — | — | — | **NONE.** Analytics is entirely first-party (`UserEvent`). **A significant positive finding** |
| **Crash reporting** | — | — | — | — | — | **NONE** |
| **AI / LLM APIs** | — | — | — | — | — | **NONE.** No AI provider anywhere in the dependency set |
| **Payment providers** | — | — | — | — | — | **NONE** |

**Notable absences that are genuinely good news:** no third-party analytics SDK, no crash reporter, no ad SDK, no AI API, no session-replay tool. The data estate is unusually self-contained for a consumer app. **The problem is not the number of third parties — it is that the one that matters most (the host) is in the wrong country and has no agreement on file.**

## 15.2 What third-party processing requires

| Requirement | Applies to | Status |
|---|---|---|
| **Privacy-policy disclosure** of each recipient | Hostinger, Google (FCM), Google (SMTP), Google/Cloudflare STUN | **ABSENT** — no policy exists |
| **Consent** | Push notifications (FCM); analytics; **any transfer abroad requires the data subject's consent** | **ABSENT** |
| **Contractual protections / DPA** | Hostinger (processor — the critical one), Google | **NOT FOUND for any provider** |
| **Cross-border transfer assessment** | Every provider above | **NOT PERFORMED** |
| **PDPC licence/permit** for cross-border transfer | Germany (primary), Google (US/global) | **ABSENT** — Part 16 |
| **Destination countries named in the licence** | Germany + Google's jurisdictions | **ABSENT** |
| Local representative if no Egyptian presence | Depends on Part 2 | **UNKNOWN** |
| Confidentiality binding on all personnel | Anyone with server or DB access | **NO EVIDENCE** |

**Action, in order:** (1) execute a written data-processing agreement with Hostinger, or migrate; (2) confirm Google Cloud/Firebase data-processing terms are accepted under the correct legal entity; (3) replace the consumer Gmail SMTP with a business transactional-email service under contract; (4) consider replacing Google/Cloudflare STUN with the **self-hosted STUN already running in LiveKit** — this removes two international recipients of raw client IPs at essentially zero cost and is the cheapest cross-border reduction available; (5) obtain and place the reverse-proxy configuration under review.

---

# PART 16 — International Data Transfers (HIGH PRIORITY)

## 16.1 The finding

**On the available evidence, essentially all Cocorra personal data is processed outside Egypt, and the primary database is among it.**

`152.239.115.176` — the single host named as the SQL Server endpoint (`appsettings.json:17`), the MinIO endpoint (`:67`), and the LiveKit/TURN server (`livekit/livekit.yaml:3`) — geolocates to **Frankfurt am Main, Hesse, Germany**, operated by **Hostinger International Limited (AS47583)**.

**Evidential caveat, stated plainly:** IP geolocation is strong but not conclusive evidence of physical server location. **Verify the contracted data-centre region directly with Hostinger and obtain it in writing.** If the server is in fact in Egypt, the analysis in this Part changes fundamentally — but the Google/FCM and Gmail transfers remain either way.

## 16.2 Data-flow map

```
                        EGYPT (data subjects)
                              │
        ┌─────────────────────┼─────────────────────────┐
        │ HTTPS/REST + SignalR│                         │ WebRTC media
        │                     │                         │ (DTLS-SRTP)
        ▼                     ▼                         ▼
╔═══════════════════════════════════════════════════════════════════════╗
║  VPS 152.239.115.176 — FRANKFURT, GERMANY (Hostinger, AS47583)        ║
║                                                                       ║
║  Cocorra API (:5000→8080, Docker)                                     ║
║      ├── SQL Server :1433 ── all relational PD                        ║
║      │       users, messages, reports, support, devices,              ║
║      │       rooms, participants, UserEvents (IpHash, UA)             ║
║      ├── MinIO :9000 (PLAIN HTTP) ── voice samples, photos,           ║
║      │       room images, report/ticket screenshots                   ║
║      │       └── exposed as https://storage.cocorraapp.com/...        ║
║      └── LiveKit SFU :7880/7881 + TURN :3478/:5349                    ║
║              ALL live audio transits here (ice_lite, no P2P)          ║
║  Docker volume cocorra-uploads; firebase-config.json (mounted)        ║
╚═══════════════════════════════════════════════════════════════════════╝
        │                     │                         │
        │ FCM push            │ SMTP :587               │ STUN :3478
        ▼                     ▼                         ▼
  ┌───────────────┐   ┌────────────────┐   ┌──────────────────────────┐
  │ GOOGLE / FCM  │   │ GOOGLE / Gmail │   │ GOOGLE + CLOUDFLARE STUN │
  │ US / global   │   │ US / global    │   │ global                   │
  │ token +       │   │ email + OTP    │   │ raw client IPs           │
  │ MESSAGE TEXT  │   │                │   │ (direct from devices)    │
  └───────────────┘   └────────────────┘   └──────────────────────────┘

  ┌──────────────────────────────────────────────────────────────────┐
  │ GITHUB (US) — source tree containing LIVE PRODUCTION SECRETS      │
  │ + SSH deploy key + APPSETTINGS_JSON secret                        │
  └──────────────────────────────────────────────────────────────────┘
```

**Note what the diagram makes obvious:** there is **no Egyptian infrastructure in the data flow at all**. The only Egyptian element is the data subjects.

## 16.3 Transfer-by-transfer analysis

| # | Data type | Destination | Purpose | Legal mechanism required | Present? | Risk | Required action |
|---|---|---|---|---|---|---|---|
| T1 | **All relational PD** — identity, DMs, reports, support, devices, participation, hashed IPs | Germany (Hostinger) | Primary database | PDPC **licence/permit** + **data subject consent** + processor agreement + destination named in licence | **NO** | **CRITICAL** | Obtain licence + consent, or **relocate to Egypt** |
| T2 | **Voice verification samples (sensitive/biometric)** + profile photos | Germany (MinIO) | Verification, display | Everything in T1 **plus** the sensitive-data licence conditions | **NO** | **CRITICAL** | Same; plus stop retaining samples (Part 5) |
| T3 | **Live audio content, in real time** | Germany (SFU/TURN) | Audio relay | Transfer authorisation; disclosure | **NO** | **HIGH** | Disclose; licence; consider an Egyptian media node |
| T4 | Report/ticket screenshots (may contain third parties' data) | Germany (MinIO) | Moderation | As T1 | **NO** | **HIGH** | Same |
| T5 | FCM token + **message text** | Google, US/global | Push | Transfer authorisation + **consent for push** | **NO** | **HIGH** | Consent; consider sending *only* a generic body, keeping content off Google |
| T6 | Email address + OTP + notification text | Google, US/global | Email | Transfer authorisation | **NO** | **HIGH** | Contracted business email provider; disclose |
| T7 | **Raw client IPs, sent directly from users' devices** | Google + Cloudflare STUN | NAT traversal | Transfer authorisation; disclosure | **NO** | **MEDIUM** | **Drop external STUN; use the self-hosted STUN already running.** Cheapest win available |
| T8 | Backups | **UNKNOWN — no backup configuration found anywhere** | — | As T1 | **UNKNOWN** | **HIGH (unknown)** | **Establish whether backups exist, where, encrypted, retained how long.** An unknown backup is an unknown breach surface and an unknown erasure gap |
| T9 | Production secrets (not user data, but the keys to all of it) | GitHub, US | Version control | — | — | **CRITICAL** | Rotate everything; purge history (Part 24) |

## 16.4 Legal analysis

**The rule.** Personal data collected or prepared for processing in Egypt may not be transferred, stored, shared or processed outside Egypt without **(a)** a licence or permit from the competent authority, and **(b)** the data subject's informed, specific consent, save for the narrow statutory exceptions. Transfers are permitted **only to countries expressly identified in the licence or permit**; adding a destination requires amendment or renewal. The authority assesses adequacy by reference to the existence of data-protection legislation consistent with Egyptian law, technical and security measures, and legal mechanisms for compensation. Disclosure to a foreign controller/processor requires a licence and a protection level not lower than Egypt's. Licensing applications must identify the destination country, the foreign entity, data categories, security systems, **storage locations**, transfer purpose and retention periods. Cross-border transfer authorisation is priced at **50% of the applicable controller/processor licensing fee**.

> Sources: [Legal500 — Overview of the Executive Regulations](https://www.legal500.com/developments/thought-leadership/overview-of-the-executive-regulations-of-the-egyptian-personal-data-protection-law/); [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt); [DLA Piper](https://www.dlapiperdataprotection.com/?t=law&c=EG); [Clyde & Co](https://www.clydeco.com/en/insights/2026/01/egypt-regulatory-update-on-data-privacy).

**Applying it:**

1. **The adequacy limb is Cocorra's strongest card.** Germany is an EU member state subject to the GDPR — a regime that, on any sensible view, provides protection not lower than the PDPL. Cocorra should expect the *adequacy* assessment for T1–T4 to be arguable in its favour. **That does not cure anything else.**
2. **The licence limb fails outright.** No licence exists, and it cannot be applied for without a commercial registration (Part 2). Germany is not named in any licence because there is no licence.
3. **The consent limb fails outright.** No consent of any kind is collected (Part 4). Informed, specific consent to an international transfer is impossible when no privacy policy discloses that a transfer occurs.
4. **The exceptions do not help.** None of the enumerated exceptions — life/medical, legal proceedings, contract in the subject's interest, judicial cooperation, legal obligation, public interest, cross-border monetary transfer, treaty — covers "we host in Frankfurt because it was convenient". The ER require exceptions to be construed restrictively.
5. **Criminal exposure.** Transferring personal data abroad without the required approvals is among the conduct criminalised by PDPL Arts. 35–40, and where sensitive data is involved reported fines reach **EGP 5,000,000** with imprisonment. Voice samples are being transferred, and they are sensitive.

## 16.5 The strategic decision the founders must make

There are two lawful routes, and they should be chosen deliberately rather than by drift:

| | **Route A — Localise** | **Route B — Licence the transfer** |
|---|---|---|
| **What** | Move the API, SQL Server, MinIO and LiveKit to an Egyptian data centre | Keep Frankfurt; obtain the PDPC licence + cross-border authorisation naming Germany; collect explicit transfer consent |
| **Removes** | T1, T2, T3, T4, T8 entirely | Nothing — manages them instead |
| **Still needed** | Licence for sensitive-data processing; consent; T5–T7 still cross-border | Everything, plus ongoing licence maintenance and destination management |
| **Cost** | Migration project; likely higher hosting cost; Egyptian data-centre latency is *better* for Egyptian users | Licence fee (**50% of the controller/processor fee**, itself volume-scaled: exempt up to 10,000 records, EGP 200 to 200,000 records, rising to a cap of EGP 2,000,000 above 5 million records); legal fees; renewal every three years |
| **Regulatory posture** | Substantially simpler; a strong signal to the PDPC | Defensible but requires sustained compliance capability |
| **Latency benefit** | **Yes — meaningful.** Frankfurt adds roughly 50–80 ms RTT for Egyptian users on a real-time audio product | — |

**Recommendation:** Route A for the database and object storage (the sensitive-data stores), because it removes the highest-severity transfers outright and is cheaper than maintaining a licensed transfer of biometric data. Consider retaining a European media node only if measured audio quality demands it — and licence that narrow transfer specifically. **Either way, the licence application and consent capture are unavoidable**; only the number of destinations changes. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED` on whether Route B is realistically obtainable for biometric data within the transition window.

---

# PART 17 — Data Retention & Deletion

## 17.1 Current retention reality

**One retention period is implemented in the entire product.** Everything else is retained forever by default.

| Data | Current retention | Enforced by | Legal assessment |
|---|---|---|---|
| **Raw `UserEvent` rows** | **180 days** | `EventCleanupService`; `RawEventRetentionDays: 180` (`appsettings.json:86`) | **The only implemented period.** Genuinely good |
| Users (`ApplicationUser`) | Indefinite | — | Fails storage limitation; no inactivity policy |
| **Voice verification samples** | **Indefinite, and survive account deletion** | — | **Worst finding.** Sensitive/biometric, purpose completed within days |
| Profile photos / room images | Indefinite; survive deletion | — | Fails |
| Report/ticket screenshots | Indefinite | — | Fails |
| **Device registry + blocks** | Indefinite while account lives; **`Cascade`-deleted with the user** (`AppDbContext.cs:200-204`) | EF cascade | See 17.3 — **the answer is the opposite of what the brief anticipates** |
| Reports | Indefinite; `ReportedUserId` `SetNull` on that user's deletion | EF | Fails limitation **and** destroys evidence |
| Moderation actions | Only as `UserEvent` → **self-delete at 180 days** | `EventCleanupService` | **An audit trail with a 180-day fuse** |
| Authentication logs | **Do not exist** | — | Cannot investigate compromise |
| IP addresses | Hashed only, 180 days | as events | **Good** |
| Security logs | Do not exist | — | Gap |
| Docker container logs | ~30 MB rolling (`10m` × 3) | Docker | Effectively minutes-to-hours under load |
| Notifications | Indefinite; **deleted** on account deletion | `AuthServices.cs:576-578` | Deletion correct; no ongoing period |
| Rooms | Indefinite; **not** deleted on host deletion (`Room.HostId` is `Restrict`) | — | Fails, **and blocks erasure** |
| Room participants | Indefinite; **not** deleted (`Restrict`) | — | Fails, **and blocks erasure** |
| Messages | Indefinite; **deleted** on either party's deletion | `AuthServices.cs:568-570` | Deletion works; **note it destroys the *other* party's copy of the conversation too** — a genuine competing-rights problem to raise with counsel |
| Support tickets/chats/messages | Indefinite; `UserId` `SetNull`, **`ContactEmail` retained** | EF | Identifier survives erasure |
| Aggregate analytics (`Daily*Metrics`) | **Indefinite, no cleanup** | — | Acceptable **only if non-identifying**; `DailyHostMetrics` is keyed by `HostId`, so it is **not** |
| `DailyStateSnapshot`, `DeadLetterEvent` | Indefinite | — | `DeadLetterEvent` may contain event payloads — **review** |
| Payment records | N/A | — | — |
| **Backups** | **UNKNOWN — none found** | — | **Unknown retention, unknown erasure gap, unknown breach surface** |

## 17.2 Recommended retention framework

Categories, with reasoning. **Periods marked `[COUNSEL]` cannot be set without legal input** — principally because Part 9's service-provider question and Part 14's accounting-records question are unresolved.

| Category | Recommendation | Basis |
|---|---|---|
| Voice verification sample | **Delete immediately on verification decision.** Retain only outcome + timestamp | Minimisation; sensitive data; purpose exhausted |
| Profile photo | Delete on account deletion; make **optional** at registration | Minimisation |
| Active user account data | Life of account + a short grace window (e.g. 30 days) for accidental-deletion recovery | Contract |
| Inactive accounts | Notify then delete/anonymise after a defined dormancy (e.g. 24 months) | Storage limitation |
| Messages | Delete on account deletion (**as now**), but resolve the counterparty-copy question | Contract; competing rights |
| Reports & moderation records | **Retain longer than the account** under a legal-hold flag: `[COUNSEL]` — likely 12–24 months post-resolution, longer if under investigation | Legitimate interest; evidence; safety |
| Moderation/admin action log | `[COUNSEL]`; **not** on the 180-day event prune | ER inspection registers |
| Device registry (unblocked) | Delete on account deletion; prune rows unseen for e.g. 12 months | Minimisation |
| **Device blocks (`IsBlocked=true`)** | **Retain beyond account deletion, pseudonymised** — see 17.3 | Safety; fraud prevention |
| Authentication logs | 90 days `[COUNSEL]` | Security |
| Hashed IPs / events | **Keep 180 days** | Already sound |
| Aggregate metrics | Indefinite **only after** removing `HostId`/`UserId` keys or truly aggregating | Anonymised data is out of scope |
| Support records | `[COUNSEL]`; **clear `ContactEmail` on account deletion** | Minimisation |
| Consent records | Limitation period + margin `[COUNSEL]`, legal-hold | Evidence of contract/consent |
| Accounting records | `[COUNSEL] — Egyptian tax accountant` | Statutory |
| Backups | Define, encrypt, retain e.g. 30 days, and **document how erasure propagates** | Erasure integrity |

## 17.3 `BlockedDevices` — the specific question in the brief, answered

The brief asks whether **keeping historical device identifiers after account deletion creates legal risk**. On the current implementation, **it does not — because they are not kept.** `BlockedDevices.ApplicationUserId` is configured `DeleteBehavior.Cascade` (`AppDbContext.cs:200-204`), so every device row, **including `IsBlocked = true` rows**, is destroyed when the user is deleted.

That inverts the expected finding, and produces two conclusions:

**(1) The privacy risk the brief anticipated is absent.** There is no orphaned device-identifier archive. Good.

**(2) A safety and integrity defect exists instead, and it is worse.** A banned user can delete their own account and thereby **erase the device block that was preventing their return**. The ban-evasion control defeats itself. Worse, `DeleteAccountAsync` is reachable by any authenticated `Active` user — and a banned user is locked out, so in practice they would need to delete *before* being banned; but the same cascade also means that **any** deletion wipes the device history that moderation relies on, and an admin cannot later establish that a returning device was previously banned.

There is also a **latent conflict** with the `[Authorize]` default policy requiring `VerificationStatus == Active`: a banned user cannot call `DeleteAccount`, so their block survives — but that is an accident of the authorisation policy, not a designed safeguard, and it would silently break if the policy changed.

**Recommendation.** Split the two concerns the table currently conflates (which the model's own comment acknowledges: *"Doubles as the device registry and the device blocklist"*):
- **Registry rows** (`IsBlocked = false`) — genuinely user-linked; delete with the user. Cascade is correct here.
- **Block rows** (`IsBlocked = true`) — a **safety record about a device, not about a person**. Retain beyond account deletion, with the user linkage **severed or pseudonymised** so the surviving row identifies a blocked device without identifying the former user. Set a defined retention period (`[COUNSEL]`, e.g. 12–24 months) rather than indefinite.

This satisfies minimisation (no personal linkage retained), preserves the safety function, and gives a defensible answer if the PDPC asks why device identifiers outlive accounts. **Raise the pseudonymisation design with counsel** — a device identifier retained without a user link is arguably still personal data, and the balance between safety legitimate-interest and minimisation should be documented, not assumed.

## 17.4 Anonymisation vs aggregation

| Should be anonymised | Should be aggregated | Should be legal-held |
|---|---|---|
| `UserEvent` beyond 180 days, if any analytic value remains | Platform/room/funnel metrics — genuinely aggregate | Reports and moderation records under investigation |
| `DailyHostMetrics` — currently identifies hosts | Cohort and retention analysis | Consent and terms-acceptance records |
| Device blocks after account deletion | Peak-hour and participation stats | Records subject to a law-enforcement preservation request |
| `DeadLetterEvent` payloads | — | Accounting records (on monetization) |

---

# PART 18 — User Rights

## 18.1 Rights under Egyptian law, mapped to actual functionality

Rights are those identified in the PDPL and ER: **access**; **withdrawal of consent**; **correction/amendment/updating/addition/deletion**; **limitation of processing to specified purposes**; **to be informed of breaches**; **objection** to processing conflicting with fundamental rights. Note the ER permit a **service fee** set by the Centre for exercising rights, **except** the right to be informed of a breach.

> Sources: [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt); [DLA Piper](https://www.dlapiperdataprotection.com/?t=law&c=EG).

| Right | Applicable? | How does a user request it? | API? | Admin workflow? | Related data | Backups | Logs | Moderation records | Device records | Verdict |
|---|---|---|---|---|---|---|---|---|---|---|
| **Access** | **Yes** | **No mechanism.** `GET /api/Profile/me` returns *some* profile fields only — not messages, events, reports, devices, or the voice sample | **Partial/no** | **None** | Not covered | Not covered | Not covered | Not covered | Not covered | **FAILS** |
| **Correction / update** | **Yes** | `PUT /api/Profile/update`; `update-avatar-preset`; `upload-picture`; `UpdatePassword` | **Yes, partial** | None | Name, bio, photo, MBTI | n/a | n/a | Cannot correct a report about them | Cannot correct | **PARTIAL — the best-served right** |
| **Deletion / erasure** | **Yes** | `DELETE` account endpoint exists but **fails for most users** | **Broken** | **None** | See Part 23 | **Unknown — no backup policy** | Events `SetNull` (retains behavioural row, unlinked); container logs unaffected | `ReportedUserId` `SetNull`; **`ReporterId` blocks deletion** | `Cascade` — deleted | **FAILS — Part 23** |
| **Withdrawal of consent** | **Yes** | **No mechanism** — nothing to withdraw, since nothing was consented to | **No** | None | — | — | — | — | **FAILS** |
| **Limitation of processing** | **Yes** | **No mechanism.** A user cannot switch off analytics, push, or profile visibility | **No** | None | — | — | — | — | **FAILS** |
| **Objection** | **Yes** | **No mechanism**; no intake channel other than a general support ticket | **No** | None | — | — | — | — | **FAILS** |
| **To be informed of a breach** | **Yes** | **No capability** — see Part 25 | **No** | **None** | — | — | — | — | **FAILS** |
| Portability | **Not clearly established in the PDPL** as a standalone right — `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`. The access right's reference to "obtain" personal data may carry a machine-readable-copy expectation | No export exists in any format | **No** | None | — | — | — | — | **ABSENT** |

## 18.2 Cross-cutting failures

1. **No rights-request intake channel at all.** There is no privacy contact address, no in-app request flow, no DPO. Support tickets are the only route, and support staff have no defined procedure, no verification step, and no SLA.
2. **No identity verification procedure for requests** — a real risk in both directions: acting on an impostor's deletion request, or refusing a genuine one.
3. **No response deadline tracked.** The reviewed sources did not specify a statutory response period; `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED` — **obtain the deadline from counsel**, since the ER contemplate registers of data-subject requests and the SLA must be designed to it.
4. **No register of requests and outcomes** — expressly contemplated by the ER as an inspectable record.
5. **Backups are an unknown** (T8, Part 16). If backups exist, no right can be honoured completely, and Cocorra cannot answer "was the data deleted?" truthfully.
6. **A banned user cannot exercise any right in-product** — locked out, and unable even to read their own notifications (`README.md:217-219`). The only channel that reaches them is email, and no procedure uses it. This is a rights failure specifically affecting the users most likely to complain to the regulator.

## 18.3 Minimum build to make rights operable

Listed for scoping only:
1. **Privacy contact point + DPO** (statutorily required in any event).
2. **`GET /api/Privacy/my-data`** — a complete machine-readable export covering profile, messages, rooms/participation, reports filed, support history, devices, consents, and events.
3. **Working deletion** — Part 23's fixes, plus file deletion, plus a documented list of what is retained under legal hold and why.
4. **Consent centre** — grant/withdraw analytics, push, and optional processing, honoured at the point of processing.
5. **Rights-request register** — request, requester, verification method, decision, date, outcome.
6. **An out-of-band channel that works for banned users** (verified email flow).
7. **Breach-notification capability** (Part 25).

---

# PART 19 — Privacy Policy Gap Analysis

## 19.1 Threshold finding

**No privacy policy exists.** A repository-wide search for `privacy polic`, `terms of use`, `user agreement`, `data protection officer`, `PDPL` and `151/2020` across all file types returns matches only in engineering documents (`USER_TRACKING_PLAN.md`, `fcm_bug_report.md`, `backend_fcm_rebuild_guide.md`) and in two service files' code comments. There is no user-facing policy in the repository, no policy endpoint, no policy URL in configuration, and no policy version anywhere.

The brief asks for a gap analysis comparing the policy against the implementation. **The gap is total**, so the analysis below is reframed usefully: it is the **disclosure inventory** — the complete list of what a compliant policy must state, derived from the implementation, so that the policy can be drafted against evidence rather than boilerplate. This is the most valuable form the deliverable can take.

## 19.2 `Privacy Policy Gap Analysis` — required disclosures vs. implementation

| Required disclosure | What the implementation actually does | Currently disclosed? |
|---|---|---|
| **Controller identity, legal name, registered address** | **Unknown** — no entity identified (Part 2) | **NO** |
| **Contact point / DPO** | Only `cocorra02@gmail.com`, a personal Gmail also used as the seeded admin login | **NO** |
| Data categories: name, email, **age**, MBTI, bio, photo | All collected; photo and voice **mandatory** at registration | **NO** |
| **Voice recording collected, stored, and human-reviewed to decide account eligibility** | `VoiceVerificationPath`; admin sets `Pending → Active/Rejected/ReRecord` | **NO — most serious omission** |
| **That the voice sample is retained indefinitely and survives account deletion** | `DeleteAccountAsync` never deletes it | **NO** |
| **That live room audio is relayed through Cocorra's servers and is NOT recorded** | True: SFU + TURN, `ice_lite`, no egress | **NO** |
| That in-room "private" messages are **persisted** to the same store as DMs | `RoomHub.cs:1016` → `SaveMessageAsync` | **NO — actively misleading by omission** |
| Messages retained until either party deletes their account | `AuthServices.cs:568-570` | **NO** |
| Device identifier + device metadata collected on **every** login and refresh | `X-Device-*` → `BlockedDevices`; `AuthServices.cs:635` | **NO** |
| Purpose of device collection (ban-evasion prevention) | `DeviceBlockingMiddleware` | **NO** |
| IP addresses **hashed, never stored raw**, kept 180 days | `UserEvent.IpHash`; `RawEventRetentionDays` | **NO — and this is a favourable fact worth publishing** |
| User-Agent stored 180 days | `UserEvent.UserAgent` | **NO** |
| Behavioural analytics: sessions, room joins, mic activation, speaking time | `UserEvent`; `TotalSpokenSeconds` | **NO** |
| **A session cookie (`CocorraSessionId`, 7 days) is set for analytics** | `SessionTrackingMiddleware.cs:25-34` | **NO** |
| Profile data visible to other users | `PublicProfileDto` | **NO** |
| Reports about a user are stored, including free text and screenshots | `Report` | **NO** |
| **Admins and users holding the `Coach` role can access admin surfaces** | `AdminController.cs:17` class-level `[Authorize(Roles = "Admin,Coach")]` | **NO** |
| Support tickets may be submitted anonymously but retain `ContactEmail` | `SupportTicket.cs:19` | **NO** |
| **Push notifications transmit message content to Google** | `MessagePreview.ForNotificationBody` | **NO** |
| **Email (including OTP) is sent via Google Gmail** | `EmailSettings` | **NO** |
| **Client IPs are sent to Google and Cloudflare STUN servers** | `appsettings.json:50-52` | **NO** |
| **All data is stored and processed in Germany** | VPS geolocation | **NO — the most legally consequential omission** |
| Legal basis for each purpose | None determined | **NO** |
| **Retention periods per category** | Only events (180 days) | **NO** |
| **All data-subject rights and how to exercise them** | Mostly not implemented (Part 18) | **NO** |
| **Right to complain to the Personal Data Protection Centre** | — | **NO** |
| Children's policy / minimum age | No age gate exists | **NO** |
| Security measures | Mixed; several critical failures (Part 24) | **NO** |
| Breach notification commitment | No capability (Part 25) | **NO** |
| Policy version and effective date | — | **NO** |
| Change-notification mechanism | `Notification` + FCM exist but unused for this | **NO** |

## 19.3 Traps to avoid when the policy is drafted

Because the policy will be written from scratch, these are the statements most likely to be written **inaccurately**, creating a fresh misrepresentation risk on top of the current omission:

| Tempting statement | Why it would be false today |
|---|---|
| "We do not record your conversations." | **True for room audio** — but the mandatory voice **verification sample** *is* a recording, kept forever. State both, separately. |
| "Your data is stored securely in Egypt." | **False.** Germany. |
| "We delete your data when you delete your account." | **False.** Deletion fails for most users; voice sample and photo persist; events, reports, support tickets and rooms remain. |
| "We use industry-standard encryption." | **Misleading.** No encryption at rest; MinIO over plain HTTP; production secrets in git. |
| "We do not share your data with third parties." | **False.** Google (FCM, SMTP), Google/Cloudflare STUN, Hostinger. |
| "You can access and download your data at any time." | **False.** No export exists. |
| "This platform is for women only." | **Unenforced** — see Part 6/28. |
| "We only collect what we need." | **Contradicted** by a mandatory profile photo, an unused MBTI field, and indefinite voice retention. |

**Recommended sequence:** fix the implementation first where it is cheap (delete voice samples, delete files on account deletion, make the photo optional, drop the MBTI field, drop external STUN), *then* write the policy. Writing the policy first forces either an inaccurate policy or a public commitment to a migration that has not happened.

**Do not draft the policy yet** — per the brief, this audit stops at the gap analysis.

---

# PART 20 — Terms of Service Gap Analysis

## 20.1 Threshold finding

**No Terms of Service exist.** There is therefore no contract between Cocorra and its users, no enforceable rules of conduct, no basis for banning anyone, no intellectual-property licence for user content, no liability limitation, no governing-law clause, and no dispute-resolution mechanism.

**The practical consequences are immediate, not theoretical:**
- Cocorra permanently bans users (`DateTimeOffset.MaxValue`) **with no contractual right to do so** and no stated grounds. A banned user could credibly assert breach of contract or arbitrary deprivation of service.
- Cocorra stores, displays and transmits user-generated content — voice, photos, messages, room titles — **with no licence from the user to do so**.
- Cocorra has no contractual prohibition on recording other members, sharing private conversations, harassment, or impersonation — so its moderation actions rest on nothing written.
- There is no limitation of liability of any kind.

## 20.2 `Terms of Service Gap Analysis`

| Clause required | Present? | What the implementation assumes / requires | Risk if omitted |
|---|---|---|---|
| **Eligibility** | **NO** | Nothing enforces age or gender; both are asserted informally | **CRITICAL** — no basis for the women-only model or an age floor |
| **Account responsibilities** | **NO** | One account per email; device registry implies a no-sharing expectation | HIGH |
| **Prohibited conduct** | **NO** | `ReportCategory` implies four prohibitions; none are written | **CRITICAL** — moderation is contractually groundless |
| **User-generated content rules** | **NO** | Free-text titles, bios, messages, room images | **CRITICAL** |
| **Moderation and enforcement** | **NO** | Warn / mute 24h / ban / reject report (`AdminReportAction`) | **CRITICAL** |
| **Suspension** | **NO** | `UserStatus.Banned` + lockout, applied immediately, enforced per-request | **CRITICAL** |
| **Termination** | **NO** | Permanent lockout; no notice; no appeal; banned users can't read notifications | **CRITICAL** — likely an unfair term under CPL 181/2018 |
| **Reporting** | **NO** | `POST /Api/V1/Support/Report` | HIGH |
| **Intellectual property (Cocorra's)** | **NO** | Name, logo, code, UI | HIGH — Part 26 |
| **User content licence to Cocorra** | **NO** | Storage, display, transmission, moderation, retention all occur | **CRITICAL** — no right to host what it hosts |
| **Platform licence to user** | **NO** | App/API access | MEDIUM |
| **Disclaimers** | **NO** | No SLA; Swagger exposed in production; best-effort push | HIGH |
| **Limitation of liability** | **NO** | Mental-health and relationship content raises real harm scenarios | **CRITICAL** |
| **Indemnity** | **NO** | Users can defame, dox, or harass through the service | HIGH |
| **Dispute resolution** | **NO** | — | HIGH |
| **Governing law** | **NO** | — | HIGH |
| **Jurisdiction** | **NO** | Egyptian **Economic Courts** hold PDPL jurisdiction | HIGH |
| **Service availability** | **NO** | Single VPS, no redundancy, no status page | MEDIUM |
| **Third-party services** | **NO** | Google, Hostinger, STUN | MEDIUM |
| **Relationship to Privacy Policy** | **NO** | Neither exists | HIGH |
| **Changes to terms** | **NO** | `Notification` + FCM available but unused | HIGH |
| **Community Guidelines incorporated by reference** | **NO** | — | HIGH — Part 21 |
| **No-recording obligation** | **NO** | Technically unpreventable; must be contractual | **HIGH** — Part 5 |

## 20.3 Clauses likely to be unenforceable, unclear or risky under Egyptian law

`LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED` on each — these are the drafting questions to put to counsel, not conclusions:

1. **Total exclusion of liability.** Egyptian civil law generally does not permit exclusion of liability for gross negligence or wilful misconduct, and consumer-protection principles constrain exclusions against consumers. A blanket "no liability whatsoever" clause is likely to be read down or struck.
2. **Unilateral amendment without notice.** A clause permitting changes effective immediately, without notice, is vulnerable as an unfair term. Build notice + re-acceptance instead (Part 12).
3. **Termination without reason or appeal.** As currently implemented — permanent, unexplained, unappealable, with the user unable to access their own notifications — this is the clause most exposed under CPL 181/2018.
4. **Foreign governing law or foreign arbitration.** Choosing non-Egyptian law or an offshore forum against Egyptian consumers is of doubtful effectiveness and may be disregarded. Expect Egyptian law and Egyptian courts.
5. **Waiver of statutory rights.** Any purported waiver of PDPL rights or CPL withdrawal/refund rights will be ineffective.
6. **Perpetual, irrevocable, transferable licence over user content.** Overbroad for what Cocorra actually needs. Scope the licence to hosting, transmitting, displaying and moderating — and say it survives only as long as necessary. An overbroad grab is both bad drafting and, for **voice** content, entangled with the sensitive-data consent requirement.
7. **Consent bundled into the Terms.** Do **not** put PDPL consents in the Terms. The ER require separation and prohibit secondary use; bundling would invalidate the consent and taint the contract.
8. **Class-action or collective-redress waivers.** Import poorly; likely ineffective.
9. **"Mental health" positioning.** If rooms carry a `MentalHealth` category and hosts are called "coaches", counsel should advise whether any disclaimer is needed to avoid an implication that a regulated health or psychological service is being provided — and whether Egyptian rules on practising psychology/therapy could be engaged by a "coach" giving mental-health guidance. **This is a question the team is unlikely to have considered and it could constrain the product.**

---

# PART 21 — Community Guidelines

## 21.1 Current state

**No community guidelines exist.** The only expression of platform norms anywhere is the five-value `ReportCategory` enum. Both **Apple** and **Google** require, as a condition of distribution, that apps hosting UGC define objectionable content and behaviour and require users to accept terms before creating UGC (Part 29) — so this is simultaneously an Egyptian-law gap, a contractual gap, and a **platform-approval blocker**.

## 21.2 Required coverage, mapped to legal hooks

| Behaviour to address | Egyptian legal hook | Report category needed | Priority |
|---|---|---|---|
| Harassment | Law 175/2018 Art. 26; Penal Code sexual-harassment provisions (as amended by Law 141/2021) | exists (`Harassment`) | **CRITICAL** |
| Threats / intimidation | Penal Code | **new** | **CRITICAL** |
| Sexual harassment | Penal Code; Law 175/2018 | **new** (distinguish from general harassment) | **CRITICAL** |
| Stalking / persistent unwanted contact | Penal Code; Law 175/2018 | **new** | HIGH |
| **Doxxing / disclosing others' personal data** | **PDPL Art. 2**; Law 175/2018 Art. 25 | **new** | **CRITICAL** |
| Blackmail / sextortion | Penal Code; Law 175/2018 | **new** | **CRITICAL** |
| Impersonation | Law 175/2018 | exists (`FakeIdentity`) | HIGH |
| Fraud / scams | Penal Code; Law 175/2018 | **new** | HIGH |
| Hate speech / religious contempt | Penal Code Art. 98(f) and incitement provisions | **new** | HIGH |
| Illegal activity | general criminal law | **new** | HIGH |
| **Recording or redistributing another member's audio** | Law 175/2018 Arts. 25–26 | **new** | **CRITICAL** — Part 5 |
| **Sharing private conversations outside the platform** | PDPL; Law 175/2018 Art. 25 | **new** | **CRITICAL** |
| **Minors / underage accounts / contact with minors** | Child Law 12/1996 (as amended); PDPL children's rules | **new — priority queue** | **CRITICAL** — Part 7 |
| Spam | — | exists (`Spam`) | MEDIUM |
| Abusive behaviour generally | — | exists (`InappropriateContent`) | HIGH |
| **Ineligible account (male user, if women-only)** | contractual | **new** | HIGH — Part 6 |
| Self-harm / crisis disclosure | not a prohibition — an **escalation** path | **new — crisis routing** | **HIGH** — see below |

**The self-harm point deserves emphasis.** A platform with a `MentalHealth` room category, live voice, and an Arabic-speaking audience **will** encounter crisis disclosures. There is currently no escalation path, no crisis-resource signposting, no moderator guidance, and no record-keeping for such events. This is not primarily a legal-compliance gap — it is a duty-of-care and reputational exposure that also has legal edges (negligence; and the child-protection question in Part 7). It should be designed deliberately, with input from someone qualified, before launch.

## 21.3 Should Community Guidelines be legally incorporated into the Terms?

**Yes — incorporated by reference, not merged.**

**Reasoning:** guidelines must be revisable at operational speed as new abuse patterns appear; terms should be stable and re-papered only on material change. Incorporation by reference gives contractual force to the guidelines while allowing them to evolve. **The mechanics matter for enforceability:** the Terms should state that breach of the Guidelines is a breach of the Terms and a ground for the specific enumerated sanctions (warning / temporary mute / suspension / permanent ban); the Guidelines must be **versioned, dated and published at a stable URL**; the acceptance record (Part 12) must capture the Guidelines version too; and material tightening of the Guidelines should trigger notice, because incorporation-by-reference of terms a user never saw is exactly where enforceability arguments are lost.

---

# PART 22 — Moderation Liability

## 22.1 The legal position

`LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED — HIGH PRIORITY`

Egypt has **no equivalent of US Section 230 or the EU's hosting safe harbour**. Research did not identify a general statutory intermediary-liability shield for user-generated content, nor a codified notice-and-takedown regime conferring immunity on compliance. Equally, no provision was identified imposing strict platform liability for all user speech.

Per the brief's instruction, neither extreme should be assumed:
- **Not automatically liable** — no provision found imposing liability on a platform for content it neither authored nor knew of.
- **Not automatically immune** — no safe harbour found either, and Law 175/2018 imposes affirmative duties on "service providers" (Part 9) while Arts. 25/26 criminalise privacy- and reputation-harming conduct without expressly confining liability to the author.

**The practical risk model that follows:** exposure is likely to turn on **knowledge and response**. A platform that receives a report and does nothing is in a materially worse position than one that acts on a documented process. That makes the notice-and-action process itself the principal risk control — and Cocorra has none.

A further Egypt-specific dimension: the state has demonstrated both willingness to prosecute online speech (Part 8.3) and, under Law 175/2018, powers concerning website blocking. **A platform that cannot show a moderation process is a more attractive target for a blocking or enforcement action than one that can.** Compliance capability here is protective, not merely performative.

## 22.2 Responsibility allocation — current vs. required

| Dimension | Current position | Required |
|---|---|---|
| **Platform responsibility** | Undefined — no terms, no guidelines, no process | Define in Terms + Guidelines; act on notice; keep records |
| **User responsibility** | **Legally unallocated** — no terms means no user obligations | Terms: users responsible for their content and conduct; indemnity |
| **Moderation responsibility** | Admins/Coaches, informally, with no criteria or SLA | Documented Content Moderation Policy: criteria, escalation, timelines, sanction ladder |
| **Notice-and-action** | **Absent.** Reports land in a table with a free-text status string and no workflow | Intake → triage by severity → decision → action → notification → appeal → record |
| **Reporting system** | Exists but taxonomy inadequate (5 categories vs 17 needed) | Expand `ReportCategory`; add severity; add priority queue for minors/threats |
| **Evidence preservation** | **Absent.** No legal hold; deletion destroys or anonymises evidence | Legal-hold flag surviving account deletion |
| **Emergency situations** | **No process** — no path for credible threats to life, child endangerment, or crisis disclosure | Defined emergency runbook with named decision-maker and out-of-hours route |
| **Law-enforcement requests** | **No process, no intake point, no log** | Documented procedure (Part 34) |
| **Illegal content** | No detection, no takedown workflow | Notice-and-action; removal; preservation; referral where required |

## 22.3 Specific implementation defects with legal significance

1. **`Report.Status` is a free-form string** (`[MaxLength(50)]`, default `"Open"`), documented in the model itself as *"cannot be relied on to hold only known values."* A typed `StatusCode` was added alongside it, but the string remains authoritative for some paths. A moderation queue whose state cannot be trusted cannot evidence a process.
2. **No moderator identity on the decision.** `Report` records `ReporterId`, `ReportedUserId`, status and `ResolvedAt` — **but not who decided, or why.** Attribution exists only in a droppable, 180-day-lifespan `UserEvent`. Cocorra cannot currently answer "who banned this user and on what basis?" six months later.
3. **No appeal mechanism, and banned users are unreachable in-product** (`README.md:217-219`).
4. **`ReportedUserId` is `SetNull`** — the accused's deletion anonymises the report, destroying the evidence trail.
5. **`ReporterId` is `Restrict`** — filing a report permanently blocks the reporter's own account deletion (Part 23). Reporting abuse should not cost a user their erasure right.
6. **Coaches can reach admin surfaces** (`AdminController.cs:17`) — a Coach is an ordinary user with elevated role, and coaches are also *subjects* of reports. Part 24.
7. **No moderation SLA or metrics** — `ResolvedAt` was added for analytics, but no target exists.

---

# PART 23 — Account Deletion

## 23.1 The headline defect

**`DeleteAccountAsync` (`AuthServices.cs:539-597`) will fail for the large majority of real users, and returns a support-referral message when it does.**

The method ends hosted rooms, then `ExecuteDeleteAsync` on exactly four tables — `FriendRequests`, `Messages`, `UserBlocks`, `Notifications` — before calling `_userManager.DeleteAsync(user)`. Its own comment says *"Clean up all Restrict-FK rows to allow user deletion."* **It does not.** The following relationships are configured `DeleteBehavior.Restrict` (or `NoAction`) and are **not** cleaned:

| Table | FK behaviour | Evidence | Who is affected |
|---|---|---|---|
| **`RoomParticipants.UserId`** | `Restrict` | `AppDbContext.cs:58-62` | **Anyone who has ever joined any room** |
| **`Rooms.HostId`** | `Restrict` | `AppDbContext.cs:89-93` | **Anyone who has ever created a room** — note rooms are *ended*, never deleted |
| **`Reports.ReporterId`** | `Restrict` | `AppDbContext.cs:158-162` | **Anyone who has ever filed a report** |
| `TopicVotes.UserId` | `Restrict` | `AppDbContext.cs:79-83` | Anyone who voted on a topic |
| `RoomTopicRequests.RequesterId` / `TargetCoachId` | `Restrict` | `AppDbContext.cs:99-109` | Requesters and target coaches |
| `RoomReminders` | composite key, no behaviour set | `AppDbContext.cs:112-113` | Anyone who set a reminder — **verify the generated behaviour** |

The call is wrapped in `catch (DbUpdateException)`, which returns *"Cannot delete account due to remaining database references. Please contact support."* So the failure is **caught and reported as a support problem** rather than surfacing as a defect. On a voice-room product, joining a room is the core action — **the erasure right is therefore inoperable for essentially every engaged user**, and the failure mode is silent to the operator.

There is no admin-side deletion workflow either, so the support referral leads nowhere defined.

## 23.2 What deletion does and does not reach

| Data | Deleted? | Mechanism | Assessment |
|---|---|---|---|
| Profile row (`ApplicationUser`) | **Attempted; fails for most users** | `_userManager.DeleteAsync` | **FAILS** |
| Email | With the user row — so, usually not | — | **FAILS** |
| Phone | N/A — not collected | — | — |
| Authentication records (`PasswordHash`, `RefreshToken`) | With the user row | Identity cascade | Fails with the above |
| **Voice verification recording (file)** | **NO — never deleted** | No `DeleteVoice` call in the method | **CRITICAL** — sensitive/biometric data outliving the account |
| **Profile photo (file)** | **NO — never deleted** | No `DeleteImage` call | **HIGH** |
| Room images, report/ticket screenshots (files) | **NO** | — | HIGH |
| `BlockedDevices` (registry + blocks) | **Yes** | `Cascade` (`AppDbContext.cs:200-204`) | Works — but see Part 17.3, it destroys safety records |
| Reports filed **by** the user | **NO** — and they block deletion | `Restrict` | **Blocks erasure** |
| Reports **about** the user | **Retained, anonymised** | `SetNull` | Evidence destroyed; row retained |
| Rooms hosted | **NO** — ended, not deleted | `Restrict` | Blocks erasure |
| Room participation | **NO** | `Restrict` | Blocks erasure |
| Messages | **Yes** — all where user is sender **or receiver** | `ExecuteDeleteAsync` | Works; **but deletes the counterparty's copy too** |
| Friend requests | **Yes** | `ExecuteDeleteAsync` | Works |
| User blocks | **Yes** | `ExecuteDeleteAsync` | Works — **also destroys blocks *others* placed on this user** |
| Notifications | **Yes** | `ExecuteDeleteAsync` | Works |
| Support tickets / chats / messages | **Retained**; `UserId` `SetNull`, **`ContactEmail` retained** | `SetNull` | Identifier survives erasure |
| `UserEvent` (analytics) | **Retained, unlinked** | `SetNull` (`AppDbContext.cs:288-291`) — comment: *"keep anonymous event stats"* | Defensible **only if** the residual row is genuinely non-identifying; a `SessionId` + `RoomId` + `IpHash` + `UserAgent` combination may be re-identifiable. **Review** |
| Aggregate metrics (`DailyHostMetrics` keyed by `HostId`) | **Retained, still keyed by the deleted user's ID** | no cleanup | **Identifier retained after erasure** |
| Container logs | **Not addressed** | Docker rotation only | Short-lived; document it |
| **Backups** | **UNKNOWN — no backup policy found** | — | **Cannot claim erasure without knowing** |

## 23.3 The genuine conflicts — and how to resolve them

The brief asks to identify conflicts between erasure, security/fraud prevention, legal retention and moderation evidence. They are real, and the current implementation resolves them **by accident** rather than by design — which is the actual problem.

| Conflict | Current accidental resolution | Recommended deliberate resolution |
|---|---|---|
| Erasure **vs. ban evasion** | Device blocks cascade away → erasure wins, safety loses | Retain `IsBlocked=true` rows pseudonymised, defined period (Part 17.3) |
| Erasure **vs. moderation evidence** | `ReportedUserId` `SetNull` → erasure wins, evidence lost; `ReporterId` `Restrict` → evidence wins, **erasure blocked entirely** | **Legal-hold flag.** Retain the report with pseudonymised references for a defined period; delete the account; document the retention in the policy |
| Erasure **vs. the counterparty's data** | Deleting a user destroys the other party's message history | Raise with counsel: delete only the deleting user's identity linkage, or delete content but retain the conversation shell — **there is no obviously correct answer and it should be a documented decision** |
| Erasure **vs. Law 175/2018 retention** (if applicable) | Not addressed at all | Resolve Part 9 first; if the duty applies, legal-hold the mandated categories and disclose it |
| Erasure **vs. accounting records** | N/A while free | On monetization: legal-hold transaction records `[COUNSEL]` |
| Erasure **vs. contract evidence** | No consent records exist to preserve | Retain minimal pseudonymised acceptance record, legal-held (Part 12) |
| Erasure **vs. analytics** | `SetNull` retains behavioural rows | Verify non-identifiability; strip `IpHash`/`UserAgent`/`SessionId` from orphaned rows |

## 23.4 Required fixes

1. **Delete or reassign every `Restrict`-blocked row** before deleting the user — participation, hosted rooms, reports filed, topic votes/requests, reminders. Where the row must survive for evidence, **pseudonymise the FK instead of blocking the delete** (i.e. change the relationship to `SetNull` with a retained pseudonymous key).
2. **Delete the voice recording and profile photo files** (`_uploadVoice.DeleteVoice`, `_uploadImage.DeleteImage`) — the code already does this on re-record (`AuthServices.cs:506`), so the capability exists and simply is not called here.
3. **Clear `SupportTicket.ContactEmail`** on deletion.
4. **Strip `IpHash`, `UserAgent`, `SessionId`** from `UserEvent` rows on `SetNull`, or delete them.
5. **Purge or re-key `DailyHostMetrics`** rows for deleted users.
6. **Add a legal-hold flag** and honour it, with a documented retained-data list.
7. **Wrap the whole operation in a transaction** — currently four `ExecuteDeleteAsync` calls commit independently, so a failure at `DeleteAsync` leaves the user's messages, friends, blocks and notifications **already destroyed while the account survives**. That is data loss on a failed operation, and it is happening today to every user whose deletion fails.
8. **Add an admin-side deletion workflow** so the support referral resolves.
9. **Define and document backup erasure propagation.**
10. **Add a web-based deletion route** — required by Google Play (Part 29), independent of Egyptian law.

> **Note the compounding effect of (7):** the current failure path is not merely "deletion doesn't work". It is "deletion irreversibly destroys the user's messages, friendships, blocks and notifications, then fails to delete the account, then tells the user to contact support." That should be treated as a live production defect regardless of the compliance framing.

---

# PART 24 — Security / Legal Compliance

Framed as legal compliance with the PDPL Art. 4 security duty and the ER's requirement to adhere to security measures issued by the competent authority across **all devices, systems, platforms and storage media**. Per the brief, this is not a penetration test and none was performed — findings are from configuration and source inspection only.

## 24.1 CRITICAL — production credentials committed to the repository

`Cocorra.API/appsettings.json` is **tracked in git** and contains live production secrets in plaintext:

| Line | Secret | Consequence if the repository is exposed |
|---|---|---|
| `:17` | SQL Server **`sa`** account password + server IP + port 1433 | **Full read/write control of the entire personal-data database**, including every user record and every message |
| `:20` | JWT `securityKey` (`CocorraApiProject2024ByKareem24031977`) | **Anyone can forge a valid token for any user, including `Admin`.** Every authorisation control in the product is void |
| `:28` | Gmail SMTP app password | Send mail as `cocorra02@gmail.com`; intercept/redirect OTPs → **account takeover of any user** |
| `:33` | `SeedAdmin` password (`Cocorra@12345`) | The admin account is **created at every startup** from this config (`Program.cs:430`) — a known-credential admin login |
| `:46` | LiveKit `ApiSecret` | Mint media tokens; join/monitor any room; **enable recording** (Part 5) |
| `:62` | TURN static credential | Relay abuse |
| `:69` | MinIO `SecretKey` | **All voice recordings and photos** |
| `livekit.yaml:12`, `:101` | LiveKit key/secret and TURN password, again | as above |

**The repository's own `.gitignore` documents this**: *"appsettings.json still holds live secrets and is still tracked"*, and the ignore rules were added after the files were committed, so they do not protect them. The secrets are also duplicated across **six nested `Cocorra.API/publish/...` directories** of committed build output, and are present throughout git **history** — so removing them from `HEAD` is insufficient.

**Legal characterisation.** This is a failure of the PDPL Art. 4 duty to apply technical and organisational measures protecting personal data against hacking and unlawful manipulation, in respect of a dataset that includes **sensitive** (biometric) data. Depending on who has had repository access, it may already constitute a **notifiable personal-data breach** — the 72-hour clock runs from awareness, and **this audit constitutes awareness**. See Part 25.

**Required actions, in order:** (1) **rotate every secret above**, treating each as compromised; (2) recreate the seeded admin with a secret not in configuration, and change `IdentitySeeder` so it cannot create a default-credential admin in production; (3) move all secrets to environment variables / the existing `.env` mechanism — the pattern **already exists and works correctly for `Analytics:IpHashSalt`**, which proves the team can do this; (4) purge the secrets from git history and force-push, or treat the repository as permanently compromised and rotate on a schedule; (5) delete the committed `publish/` trees; (6) audit who has had repository access and when; (7) assess breach-notification obligations with counsel.

## 24.2 Full control audit

| Control | Finding | Legal relevance | Severity |
|---|---|---|---|
| **Password hashing** | ASP.NET Core Identity default (PBKDF2-HMAC-SHA512, 100k iterations, per-user salt) | Art. 4 | **ADEQUATE — a genuine strength** |
| Password policy | 8 chars, upper/lower/digit/symbol; lockout 5 attempts/15 min (`Program.cs:306-317`). `RegisterDto` says `MinLength(6)` — inconsistent but Identity wins | Art. 4 | ADEQUATE |
| **JWT** | HMAC-SHA256; issuer/audience/lifetime/signing all validated; **key in git**; `RequireHttpsMetadata = false` (`Program.cs:335`) | Art. 4 | **CRITICAL** (key) |
| **Refresh tokens** | 32 cryptographic bytes — good generation; **stored plaintext**; lookup by unindexed equality scan; replaced but not revoked-on-reuse; 7-day expiry | Art. 4 | **HIGH** |
| Token expiration | Access ~1 day; refresh 7 days; **`OnTokenValidated` re-checks lockout every request** | Art. 4 | **GOOD** — immediate ban enforcement is better than most implementations |
| MFA | **None** — including for **Admin** | Art. 4 | **HIGH** — admins can read voice samples and all messages |
| Device identification | `X-Device-*` headers; registry on login/refresh; absent header = "nothing to register", never an error | Art. 4 | MEDIUM (bypassable) |
| **Rate limiting** | Global fixed window, **100 req/min per IP** (`Program.cs:404-413`) | Art. 4 | **MEDIUM** — no stricter limit on login, OTP, password-reset or registration; 100/min is ample for credential stuffing and OTP brute force. **Add per-endpoint limits** |
| Authorization | Default policy requires `VerificationStatus == "Active"`; `VerificationOnly` for the verification flow | Art. 4 | **GOOD design** |
| **RBAC** | `AdminController` class-level **`[Authorize(Roles = "Admin,Coach")]`** (`:17`), with only 4 actions narrowed to `Admin`. **Coaches — ordinary users with a role — can reach every other admin action**, including user listing and dashboard stats | Art. 4; **excessive access to personal data** | **HIGH** — audit every action and default to `Admin` |
| **Audit logs** | **No durable admin audit log.** Attribution only via droppable, 180-day `UserEvent` (Part 3.3(24)) | ER inspection registers | **HIGH** |
| **Encryption at rest** | **None.** No SQL Server TDE, no column encryption, no MinIO SSE. Voice samples and messages stored in clear | Art. 4; sensitive data | **HIGH** |
| **Encryption in transit** | HTTPS at the edge (`UseHttpsRedirection`); WebRTC DTLS-SRTP; TURNS available — **but MinIO over plain HTTP** (`Program.cs:282`, endpoint `http://…:9000`) and **SQL Server with `TrustServerCertificate=True`** (accepts any certificate → MITM-able) | Art. 4 | **HIGH** |
| HSTS | **Not configured** — `UseHsts()` absent | Art. 4 | MEDIUM |
| **Database access** | Application connects as **`sa`** — full sysadmin. No least-privilege application account | Art. 4 | **HIGH** |
| **Secrets management** | Correct pattern exists and is enforced for `IpHashSalt` (fail-fast, no fallback, documented) — **and is not applied to anything else** | Art. 4 | **CRITICAL** |
| **Backups** | **No backup configuration found anywhere.** Neither existence, encryption, location, nor retention is evidenced | Art. 4 (availability/integrity); erasure | **HIGH (unknown)** |
| Access control (infrastructure) | Deploy is `root` over SSH to `/root/cocorra-app` (`deploy.yml`); no bastion, no per-engineer accounts evidenced | Art. 4 | **HIGH** |
| **Admin access** | Seeded from config at every startup; no MFA; no IP restriction; no session limits; **no log of admin data access** | Art. 4; sensitive data | **HIGH** |
| Production credentials | See 24.1 | Art. 4 | **CRITICAL** |
| **Swagger in production** | Enabled in **all** environments (`Program.cs:484-495`), documented as deliberate in `README.md:12` | Art. 4 (attack-surface disclosure) | **MEDIUM** — publishes the full API surface and schemas to anyone |
| Docker | Non-root user not evidenced; `firebase-config.json` bind-mounted read-only (good); uploads on a local volume; log rotation 10m×3 | Art. 4 | MEDIUM |
| **VPS security** | Single host running API + SQL Server + MinIO + LiveKit + TURN. **No network segmentation** — DB (1433) and MinIO (9000) are addressed via the **public IP**, implying they may be internet-reachable. **VERIFY the firewall** | Art. 4 | **CRITICAL if exposed** |
| **Caddy / reverse proxy** | **Not in the repository.** TLS termination for three subdomains is configured outside version control and is **unaudited** | Art. 4 | **HIGH (unknown)** — obtain and review |
| LiveKit | Self-hosted (good for data protection); secrets in repo; `use_external_ip`, `ice_lite`; webhook signature verified via SDK `WebhookReceiver` (good) | Art. 4 | HIGH (secrets) |
| Redis | **Not present** | — | N/A |
| MinIO | Plain HTTP; credentials in repo; **bucket policy not set in code but public URLs are returned and rendered → likely anonymous public read**. **VERIFY on the server** | Art. 4; sensitive data | **CRITICAL if public** |
| CORS | Explicit allow-list with credentials; **falls back to `SetIsOriginAllowed(_ => true)` if `Cors:AllowedOrigins` is empty** (`Program.cs:138-143`) — a misconfiguration silently becomes allow-all-with-credentials | Art. 4 | MEDIUM |
| `AllowedHosts` | `"*"` | Art. 4 | LOW |
| Error handling | Generic 500 in production; dev page only in Development | Art. 4 | **GOOD** |
| Input validation | DTO annotations; **`Bio`, `Report.Description`, support message bodies unbounded**; file signature + content-type checks on upload (good) | Art. 4 | MEDIUM |
| PII in logs | `[LIVEKIT-TOKEN-AUDIT]` logs identity and participant name at `Information`, self-labelled **temporary** | Art. 4; minimisation | **MEDIUM — remove** |

## 24.3 The five security items that must precede any launch

1. **Rotate every committed secret and remove secrets from configuration** (24.1).
2. **Verify and lock the VPS firewall** so 1433 and 9000 are not internet-reachable; move MinIO traffic to HTTPS or a private network.
3. **Verify and fix the MinIO bucket policy**; serve voice samples only via short-lived pre-signed URLs to authenticated admins.
4. **Replace `sa`** with a least-privilege application account; fix `TrustServerCertificate=True`.
5. **Tighten RBAC** — remove `Coach` from `AdminController`; add MFA for admins; add a durable admin-access log.

---

# PART 25 — Data Breach / Incident Response

## 25.1 Current state

**There is no incident-response process.** No breach register, no detection, no alerting, no classification, no escalation path, no notification templates, no named responsible person, no DPO, and no capability to notify the PDPC or affected users within the statutory window. Container logs rotate at ~30 MB, so forensic evidence of an incident may not survive long enough to investigate it.

**And the obligation is already engaged.** The credential exposure in Part 24.1 is not a hypothetical: live database, JWT, email, storage and media credentials have been readable by anyone with repository access for an unknown period. Whether that constitutes a notifiable breach depends on facts this audit cannot establish (who had access, whether the data was accessed). **The 72-hour clock runs from awareness.** This section should therefore be read as an urgent action item, not a policy exercise.

## 25.2 Statutory requirements

| Requirement | Detail | Source |
|---|---|---|
| **Notify the PDPC** | **Within 72 hours of knowledge** of the breach, via the designated electronic register | [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt); [Legal500](https://www.legal500.com/developments/thought-leadership/overview-of-the-executive-regulations-of-the-egyptian-personal-data-protection-law/) |
| **Immediate notification** | **"Without delay"** where national security considerations arise | same |
| **Notify data subjects** | Within **3 working days** of notifying the PDPC, by pre-agreed communication methods | [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt); [Clyde & Co](https://www.clydeco.com/en/insights/2026/01/egypt-regulatory-update-on-data-privacy) |
| **Content** | Nature and form of the breach; approximate number of affected records; **DPO contact details**; potential consequences; measures adopted and proposed; documentation and corrective actions | [DLA Piper](https://www.dlapiperdataprotection.com/?t=law&c=EG) |
| **Responsible person** | The **DPO** is the officer charged with notifying the Centre within 72 hours — Cocorra has no DPO | [DLA Piper](https://www.dlapiperdataprotection.com/?t=law&c=EG) |
| **Register** | Breaches recorded in a secure electronic register, inspectable | [Legal500](https://www.legal500.com/developments/thought-leadership/overview-of-the-executive-regulations-of-the-egyptian-personal-data-protection-law/) |
| **Fee exemption** | The right to be informed of a breach is **not** subject to any service fee | [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt) |
| Liability model | **Custodian liability** — the organisation is liable without proof of fault unless an external cause is proven | [Chambers 2026](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt) |

**The custodian-liability point is significant** and easy to miss: Cocorra does not get to argue it took reasonable care. Liability attaches unless an **external cause** is proven. That materially raises the value of preventive controls over post-hoc justification.

## 25.3 `Cocorra Data Breach Response Procedure`

Provided as a procedure to adopt, **not implemented** per the brief.

### Roles (must be filled by name before launch)
| Role | Responsibility |
|---|---|
| **Incident Lead** | Owns the incident; declares severity; authorises containment |
| **DPO** | Statutory notifications; PDPC liaison; register upkeep |
| **Technical Lead** | Investigation, containment, evidence preservation |
| **Communications Owner** | User and public communication |
| **Legal Counsel (external)** | Notification decisions; law-enforcement interface |

### Stage 1 — Detection (target: continuous)
Sources: monitoring alerts; support tickets; user reports; third-party notification; researcher disclosure; internal discovery (**including audits such as this one**).
**Gaps to close first:** no alerting exists; container logs rotate too fast for forensics; no failed-login monitoring; no MinIO/DB access logging; **no security contact address for external reporters** — a researcher who finds an exposed bucket currently has nowhere to report it.

### Stage 2 — Classification (target: within 4 hours)
| Severity | Definition | Examples relevant to Cocorra |
|---|---|---|
| **S1 — Critical** | Sensitive data exposed, or mass exposure | **Voice recordings accessed**; database exfiltration; **JWT key compromise**; MinIO bucket found public |
| **S2 — High** | Personal data of multiple users exposed | Message content leak; admin account compromise |
| **S3 — Medium** | Limited personal data exposure | Single-account takeover; misdirected email |
| **S4 — Low** | No personal data exposed | Failed intrusion; hashed-IP-only exposure |
**Rule:** any incident touching **voice samples, children's data, or message content** is **S1 or S2 by default** — these are sensitive categories and the notification threshold is presumed met.

### Stage 3 — Internal escalation (target: immediately on classification)
S1/S2: Incident Lead + DPO + external counsel notified at once; **the 72-hour clock starts at the moment of knowledge, not at the end of the investigation.** Notify on the basis of what is known; supplement later. S3: DPO within 24 hours. S4: logged in the register.

### Stage 4 — Evidence preservation (before containment where feasible)
Snapshot the VPS; export container logs **immediately** (they rotate); preserve DB/MinIO access logs; record timeline, actors and actions; set **legal hold** on affected records so deletion routines do not destroy evidence. Preserve in a location separate from the compromised system.

### Stage 5 — Containment
Rotate affected credentials; revoke sessions (`RevokeToken`, clear `RefreshToken`); force re-authentication; restrict or take offline the affected component; close the vector; **for a JWT-key compromise, rotating the key invalidates all outstanding tokens — expect a full re-login event and plan the user communication.**

### Stage 6 — Notification assessment (within 72 hours of knowledge)
Determine: categories affected; whether **sensitive** data is involved; approximate record count; identifiability; likely consequences; whether national security is implicated (→ notify **without delay**). **Default to notifying** where sensitive data may be involved. Notification must be made even if the investigation is incomplete.

### Stage 7 — PDPC notification
Via the designated electronic register, with all mandated content. **Requires a DPO to exist and be registered** — this is a hard dependency.

### Stage 8 — Data-subject notification (within 3 working days of PDPC notification)
Channels: email (works for banned users — the only channel that does), in-app `Notification` rows, FCM push. Content: what happened, what data, likely consequences, what Cocorra has done, what the user should do, contact point, right to complain to the PDPC. **No fee may be charged.**
**Capability gap:** there is currently no mechanism to send a bulk notification to a defined affected cohort. The `Notification` + push infrastructure exists and could serve this — it simply is not wired for it.

### Stage 9 — Law enforcement
Assess whether the incident is a Law 175/2018 offence warranting a report; take counsel's advice before contacting authorities; log all contact.

### Stage 10 — Post-incident documentation
Complete the breach register entry: timeline, root cause, data affected, notifications made and dates, remediation, preventive changes, lessons. Retain per the retention schedule. Review the procedure annually and after every S1/S2.

## 25.4 Prerequisites before this procedure can function

1. Appoint and register a **DPO** (statutory prerequisite for notification).
2. Establish the **breach register**.
3. Create a **security contact address** for external reports.
4. Add basic **alerting** — failed-login spikes, admin actions, MinIO/DB access anomalies.
5. **Increase log retention** beyond ~30 MB rolling, on a mounted volume — the existing `Analytics:StructuredLogPath` option already provides the hook.
6. Build a **bulk affected-cohort notification** capability.
7. **Assess the Part 24.1 credential exposure now, with counsel**, and determine whether notification is already required.

---

# PART 26 — Intellectual Property

## 26.1 Dependency licence audit

Every third-party package across all four projects, from the `.csproj` files:

| Package | Version | Licence | Copyleft risk | Assessment |
|---|---|---|---|---|
| `FirebaseAdmin` | 3.5.0 | Apache-2.0 | None | **OK** — attribution only |
| `MailKit` | 4.16.0 | MIT | None | **OK** |
| `MimeKit` | 4.16.0 | MIT | None | **OK** |
| `MediatR` | 14.1.0 | Apache-2.0 | None | **OK** — but see 26.2 |
| `Microsoft.EntityFrameworkCore` (+ `.Design`, `.Tools`, `.SqlServer`, `.InMemory`, `.Sqlite`) | 10.0.2 | MIT | None | **OK** |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.2 | MIT | None | **OK** |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | 10.0.2 | MIT | None | **OK** |
| `Swashbuckle.AspNetCore` / `.SwaggerUI` | 6.6.2 / 10.1.0 | MIT | None | **OK** |
| `AWSSDK.S3` | 4.0.23.5 | Apache-2.0 | None | **OK** |
| `Livekit.Server.Sdk.Dotnet` | 1.2.2 | Apache-2.0 | None | **OK** |
| `xunit`, `xunit.runner.visualstudio` | 2.9.3 / 3.1.4 | Apache-2.0 | None | **OK** (test only) |
| `Moq` | 4.20.72 | BSD-3-Clause | None | **OK** — the SponsorLink telemetry controversy affected 4.20.0–4.20.1 only; 4.20.72 is clean |
| `coverlet.collector` | 6.0.4 | MIT | None | **OK** (test only) |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | None | **OK** (test only) |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.13 | Apache-2.0 (SQLite core: public domain) | None | **OK** (test only) |

**Findings: no GPL, no AGPL, no LGPL, and no proprietary or commercially-licensed dependency in the .NET solution.** This is a clean permissive-licence estate and, on the evidence available, the lowest-risk area of the audit.

**Attribution obligation:** Apache-2.0 and MIT/BSD both require the licence text and copyright notices to be reproduced in distributions. **No `THIRD-PARTY-NOTICES` file exists.** For a server-side application this is a low-severity technical non-compliance; it becomes more visible if a mobile app ships (Apple/Google both expect a licences screen). **Best practice**, not an Egyptian legal requirement.

## 26.2 Infrastructure and runtime components

| Component | Licence | Assessment |
|---|---|---|
| **LiveKit server** (self-hosted) | **Apache-2.0** | **OK for self-hosting and commercial use.** Verify no separately-licensed LiveKit Cloud/Egress component is later introduced |
| **MinIO server** | **`FLAG — VERIFY`** | MinIO relicensed its server from Apache-2.0 to **AGPL-3.0** (from RELEASE.2021-04-22). **AGPL-3.0 is a network-copyleft licence.** The repository pins no MinIO version — it is deployed out-of-band, so the version and therefore the licence are **unknown**. Cocorra accesses MinIO over the S3 API via `AWSSDK.S3`, which is the ordinary and generally accepted use, and does not modify or redistribute MinIO. On that basis the practical risk is low. **But this is the only copyleft exposure in the stack and it should be checked, not assumed.** `LEGAL UNCERTAINTY — LAWYER REVIEW REQUIRED` |
| **SQL Server** | **`FLAG — VERIFY`** | **Proprietary, and this is the most likely licensing liability in the audit.** A production SQL Server instance requires a paid licence unless it is Express (10 GB database limit, feature restrictions) or Developer Edition (**strictly non-production**). The connection uses the `sa` account on a self-managed VPS. **Determine which edition is running and whether it is licensed for production use.** Running Developer Edition in production is a licence breach; exceeding Express's 10 GB limit will also cause an outage as voice files and events accumulate |
| **.NET 10 runtime** | MIT | **OK** |
| **Docker Engine** | Apache-2.0 | **OK** (Docker Desktop has separate commercial terms for larger organisations — not applicable to server deployment) |
| **Caddy** (if used) | Apache-2.0 | **OK** — but the configuration is not in the repository |
| **Hostinger VPS** | Contract | See Part 15 — **no DPA on file** |

## 26.3 Cocorra's own IP

| Asset | Ownership evidence | Registered? | Risk |
|---|---|---|---|
| **"Cocorra" name** | Used throughout; domain `cocorraapp.com` | **No trademark registration evidenced** | **MEDIUM–HIGH.** Egypt is a first-to-file jurisdiction. An unregistered mark on a consumer app is exposed to a third-party filing that could force a rebrand after launch spend. Register with the **Egyptian Trademark Office (ITDA)** in the relevant Nice classes (9, 38, 42, 45) before public launch |
| **Logo** | **Not in the repository** | Unknown | **UNKNOWN** — if designed by a contractor, **verify written assignment** (26.4) |
| **Source code** | In the repository; git author `Kareem`; sole apparent contributor | No entity assignment evidenced | **HIGH** — Egyptian copyright vests in the author. **Without a written assignment to the company, the code may be owned by the individual developer(s), not by Cocorra.** This is a standard investor-diligence blocker |
| **Designs / UI** | Mobile app is in a **separate Flutter repository not included here** (`01-product-feature-inventory.md:44`) | Unknown | **UNKNOWN — out of audit scope.** The mobile repo needs its own IP and licence review |
| **Content** | User-generated | No licence from users (Part 20) | **CRITICAL** — no right to host user content |
| **Music / audio assets** | None found | — | None |
| **Images** | Avatar presets implied by `UpdateAvatarPresetDto` — **assets not in the repository** | Unknown | **`FLAG` — determine the provenance and licence of the avatar preset images.** Stock or AI-generated assets carry different terms |
| **Fonts** | None in the repository (mobile app concern) | Unknown | **UNKNOWN** — commercial font licensing is a common oversight in app releases |
| **AI-generated assets** | None evidenced | — | If any exist, note that AI-generated works have **uncertain copyright status** in most jurisdictions including Egypt, so they may not be protectable — relevant for logo/brand assets in particular |

## 26.4 IP risk table

| ID | Risk | Basis | Severity | Action |
|---|---|---|---|---|
| IP-1 | **Source code may be owned by the individual developer, not the company** | Egyptian Copyright Law (Law 82/2002) vests copyright in the author absent written assignment | **HIGH** | Written IP assignment from every contributor to the entity; blocker for investment |
| IP-2 | **No user-content licence** | No Terms exist | **CRITICAL** | Terms with a scoped licence (Part 20) |
| IP-3 | **"Cocorra" unregistered** | First-to-file | **MEDIUM–HIGH** | File before public launch |
| IP-4 | **SQL Server edition/licence unverified** | Microsoft licensing | **MEDIUM–HIGH** | Verify edition; licence or migrate |
| IP-5 | MinIO version/licence unverified (AGPL-3.0 since 2021) | AGPL network copyleft | **MEDIUM** | Confirm version; confirm no modification/redistribution |
| IP-6 | Logo, avatar presets, fonts — provenance unknown | Copyright | **MEDIUM** | Collect licences and assignments |
| IP-7 | No third-party attribution file | Apache-2.0 / MIT notice terms | **LOW** | Generate `THIRD-PARTY-NOTICES` |
| IP-8 | Mobile (Flutter) repository not audited | — | **UNKNOWN** | Separate review required |

---

# PART 27 — Employees / Contractors / Hosts

## 27.1 What the repository evidences

| Category | Evidence | Assessment |
|---|---|---|
| Employees | **None evidenced.** Single git author (`Kareem`) | Likely founder-only or very small |
| **Moderators** | The **role exists functionally** — `Admin` and `Coach` roles seeded by `RoleSeeder`; admins review voice samples, act on reports, ban users | **Whoever performs this has access to sensitive personal data with no agreement on file** |
| **Coaches / hosts** | **`Coach` is a seeded platform role** with elevated access, not merely a user label | See 27.3 — this is the significant finding |
| Contractors / freelancers | Mobile app is in a separate repository — implies at least one other developer or team | **No agreements evidenced** |

`RoleSeeder` and `IdentitySeeder` establish `Admin`, `Coach` and `User` roles and create an admin account from configuration. So the human structure is real even though no HR or contracting documentation exists in the repository.

## 27.2 Analysis

| Dimension | Current position | Requirement | Risk |
|---|---|---|---|
| **Employment / contract structure** | Unknown; nothing evidenced | Written contracts; Egyptian Labour Law No. 12 of 2003 applies to employees | **HIGH** — `LEGAL UNCERTAINTY — LAWYER REVIEW REQUIRED` |
| **Confidentiality** | **No NDAs evidenced** | The ER require controllers to **bind all personnel to confidentiality obligations** | **HIGH — this is an express regulatory requirement, not merely prudent** |
| **IP ownership** | No assignments evidenced | Written assignment (IP-1) | **HIGH** |
| **Access to personal data** | Admins and **Coaches** can reach admin endpoints | Least privilege; confidentiality undertakings; access logging | **HIGH** |
| **Access to audio** | Admins review voice verification samples as a core workflow. **No access log exists** | Sensitive-data access must be controlled and recorded | **CRITICAL** |
| **Moderation authority** | Warn / mute / ban / reject, with no criteria, no audit trail, no appeal | Documented policy; logged decisions | **HIGH** |
| **Liability** | Undefined | Allocate contractually | MEDIUM |
| **NDAs** | None | Required (above) | **HIGH** |
| **Data access agreements** | None | Confidentiality + acceptable-use + consequences | **HIGH** |

## 27.3 Are hosts employees? — the question the brief flags, and it matters more than expected

**Per the brief, hosts are not assumed to be employees.** But the repository shows something more consequential than a classification question: **`Coach` is not a cosmetic label — it is a seeded authorisation role that grants access to `AdminController`.**

`AdminController.cs:17` applies `[Authorize(Roles = "Admin,Coach")]` at class level; only four actions narrow to `Admin`. So a Coach — a person who may be an unvetted, uncontracted, external community member — can reach the remaining admin actions, which include user listing and platform statistics.

**This creates a legal problem independent of employment classification.** Cocorra is disclosing personal data of its users to individuals who: have signed no confidentiality undertaking (contrary to the ER's express requirement); have no data-access agreement; are not vetted; are not logged when they access data; and may themselves be the subject of user reports. Whether they are employees, contractors or volunteers, **each is a person to whom personal data is being made available**, and under PDPL Art. 2 personal data may not be disclosed without a lawful basis.

**Recommended actions:**
1. **Remove `Coach` from `AdminController` entirely** and grant coaches only the narrow room-management capability they actually need (which `RoomHub` already provides through host checks). This is a small change that removes a whole category of exposure.
2. Before any coach is onboarded: **written agreement** covering confidentiality, permitted data use, prohibition on export or off-platform contact, moderation authority limits, IP assignment for any content produced, and termination.
3. **Log every coach and admin access to personal data.**
4. **Classification:** take counsel's view on whether paid hosts would be employees, independent contractors, or independent suppliers — the answer drives labour-law obligations, social insurance, and the Part 14 withholding-tax analysis. `LEGAL UNCERTAINTY — EGYPTIAN LAWYER REVIEW REQUIRED`. **Do not** design a revenue-share before this is answered.
5. If moderation is ever outsourced, the moderator becomes a **processor** requiring a data-processing agreement — and if outside Egypt, another cross-border transfer.

---

# PART 28 — Advertising / Marketing Claims

## 28.1 Scope note

No marketing site, app-store listing, or social-media copy is present in the repository. This section therefore audits (a) the product language that **is** in the codebase, and (b) the claims Cocorra is **likely** to make, flagging which would be inaccurate **today**. The purpose is preventive: to stop a claim being published that the implementation cannot support.

## 28.2 Claim-by-claim assessment

| Claim | Accurate today? | Evidence | Exposure |
|---|---|---|---|
| **"Women only"** | **NO — unenforced** | No gender field, no check anywhere (Part 6) | **CPL 181/2018 misleading claim; App Store/Play safety-claim scrutiny; user-safety harm.** The single highest-risk claim |
| **"Verified members"** | **Partially** — a human reviews a voice sample, with no documented criteria, no accuracy standard, no appeal | `UserStatus` flow | **MEDIUM** — must not imply identity verification |
| **"Safe space"** | **NO** — no terms, no guidelines, no proactive moderation, no crisis path, no appeal, minors can register | Parts 7, 8, 21, 22 | **HIGH** |
| **"Private"** | **Misleading** — in-room "private" messages are **persisted** to the same table as DMs and are admin-reachable | `RoomHub.cs:1016` | **HIGH** |
| **"Confidential"** | **NO** — no encryption at rest; admins can read all messages; credentials in git | Part 24 | **HIGH** |
| **"Anonymous"** | **NO** — real first and last name are the display identity; a profile photo is mandatory | `ApplicationUser`; `RegisterDto.cs:35` | **HIGH** if claimed |
| **"Encrypted"** | **Misleading if unqualified.** True: HTTPS in transit; WebRTC DTLS-SRTP for media. False: any implication of end-to-end encryption or encryption at rest — **messages and voice samples are stored in clear** | Part 24 | **HIGH** |
| **"End-to-end encrypted"** | **FALSE — must never be claimed.** Media traverses Cocorra's SFU/TURN with no E2EE configured; messages are stored in plaintext | `livekit.yaml`; `Message.cs` | **CRITICAL if claimed** |
| **"We never record your conversations"** | **True for room audio; false as a general statement** — the mandatory verification sample is a recording retained indefinitely and surviving account deletion | Part 5 | **HIGH** — needs careful, split phrasing |
| **"Your data stays in Egypt"** | **FALSE** | Frankfurt (Part 16) | **CRITICAL if claimed** |
| **"GDPR/PDPL compliant"** | **FALSE — must not be claimed** | This audit | **CRITICAL if claimed** |
| **"Delete your account any time"** | **FALSE — deletion fails for most users** | Part 23 | **HIGH** |
| **AI / smart matching claims** | **No AI exists in the product** | No AI dependency | **CRITICAL if claimed** |
| **"Coaches" / "coaching"** | Descriptive, but see Part 20.3(9) — may imply a regulated service | `RoomCategory.MentalHealth` | **MEDIUM** — `LEGAL UNCERTAINTY` |
| **"Mental health support"** | **Must not be claimed** — hosts are unvetted for clinical competence and no crisis pathway exists | `RoomCategory.MentalHealth` | **HIGH** |

## 28.3 Legal hooks

- **Consumer Protection Law No. 181 of 2018** prohibits misleading claims and, for distance contracting, requires accurate disclosure of the service's essential characteristics and any associated risks. Marketing a "safe, private, women-only" space that is none of those things engages this directly, and the **Consumer Protection Agency** can act on complaints.
- **PDPL** — a privacy statement that misdescribes the processing is not merely a marketing problem: it invalidates the transparency basis on which any consent rests.
- **Platform policies** — Apple and Google both police safety and privacy claims; unsupported claims are an app-review rejection risk and a post-launch removal risk (Part 29).

## 28.4 Rule to adopt

**No safety, privacy, security or eligibility claim should be published unless a named person can point to the implementation that delivers it.** Concretely, before launch: either implement women-only enforcement or reframe the positioning; describe the voice sample honestly; never claim E2EE, anonymity, Egyptian data residency, encryption at rest, or working deletion until each is true. Where a claim is aspirational, say so in the future tense — a roadmap statement is not a misrepresentation; a present-tense one is.

---

# PART 29 — App Store / Google Play Requirements

The brief asks that platform requirements be separated from Egyptian law. They are **contractual conditions of distribution**, not law — but they are **launch blockers**, and several overlap with PDPL obligations, which is useful: one fix satisfies both.

## 29.1 Apple App Store

| Requirement | Guideline | Cocorra status | Blocker? |
|---|---|---|---|
| **UGC moderation** — filter objectionable material, a mechanism to report with timely response, block abusive users, published contact information | **1.2** | Reporting ✅; blocking ✅; **no filter, no response SLA, no published contact, no guidelines** | **YES** |
| Apps used primarily for objectionable UGC, anonymous/random chat, or objectification are removed | **1.2** | Not the design intent — but **live unmoderated voice with no guidelines is exactly the profile reviewers scrutinise** | RISK |
| **Account deletion in-app** — all tokens and data associated with the account, **including user-generated content**, must be removed | **5.1.1(v)** | Endpoint exists but **fails for most users**; voice sample and photo are **never** deleted | **YES** — Part 23 |
| **Privacy policy link** in the listing and in-app | **5.1.1** | **Does not exist** | **YES** |
| **Privacy nutrition labels** — accurate declaration of all collected data | **5.1** | Would need to declare: contact info, user content, **audio data**, identifiers, usage data, diagnostics | **YES** — cannot be completed accurately today |
| **Purpose strings** for microphone permission | **5.1.1** | Client-side (Flutter repo) | Verify |
| **Age rating** | **1.1** | Unmoderated live UGC + DMs → **17+** likely; must match the enforced minimum age | **YES** — Part 7 |
| **Kids Category** | **1.3** | Must **not** be used | — |
| Login/registration only where required | **5.1.1(i)** | Justifiable (social features) | OK |
| Account-holder data minimisation | **5.1.1** | **Mandatory profile photo and voice recording will draw scrutiny** | RISK |
| Data-collection consent | **5.1.1(ii)** | **No consent mechanism** | **YES** |
| In-app purchase for digital content | **3.1.1** | N/A now; **mandatory on monetization** (Part 13, item 19) | Future |

## 29.2 Google Play

| Requirement | Policy | Cocorra status | Blocker? |
|---|---|---|---|
| **UGC moderation** — users must accept the app's terms/user policy **before** creating or uploading UGC; objectionable content and behaviour must be **defined**; moderation must be robust, effective and ongoing | User Generated Content | **No terms to accept; nothing defined; no proactive moderation** | **YES** |
| In-app reporting and blocking | UGC | Both exist | OK |
| **Account deletion in-app *and* via a web resource** | User Data | **In-app broken; no web route at all** | **YES** |
| **Data safety form** — accurate declaration of collection, sharing, security practices | User Data | Would need to declare **audio, messages, identifiers, and sharing with Google** | **YES** |
| **Privacy policy** in listing and in-app | User Data | **Does not exist** | **YES** |
| Sensitive permissions justified | Permissions | Microphone — justifiable | OK |
| **Content rating questionnaire** | Ratings | Must reflect live unmoderated voice, UGC and DMs | **YES** |
| Families policy | Families | Must **not** target children; age gate must work | **YES** — Part 7 |
| Health claims / misleading claims | Misrepresentation | `MentalHealth` category + any "support" claim → scrutiny | RISK — Part 28 |
| Play Billing for digital purchases | Payments | N/A now; mandatory later | Future |

> Sources: [Apple App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/); [Apple — account deletion requirement](https://developer.apple.com/news/?id=12m75xbj); [Google Play — account deletion](https://support.google.com/googleplay/android-developer/answer/13327111); [Google Play — User Generated Content](https://support.google.com/googleplay/android-developer/answer/9876937); [Google Play — Developer Program Policy](https://support.google.com/googleplay/android-developer/answer/16933379).

## 29.3 Separation of requirements

| **Egyptian legal requirement** | **Apple/Google platform requirement** | **Both** |
|---|---|---|
| PDPC licence / permits | In-app purchase for digital content | **Privacy policy** |
| Cross-border transfer authorisation | Privacy nutrition labels / Data safety form | **Working account deletion** |
| Explicit written consent for sensitive data | Age rating and content-rating questionnaires | **Terms accepted before UGC creation** |
| Guardian consent for under-15s | Web-based deletion route | **Defined objectionable content and behaviour** |
| 72-hour breach notification | Purpose strings | **Report and block mechanisms** |
| DPO appointment | Kids Category restrictions | **Accurate claims** |
| Data-subject rights | Play Billing | **Published contact information** |
| Consumer-protection disclosures | | **An enforced minimum age** |

**Useful observation:** the "Both" column is where effort compounds. Privacy policy, working deletion, terms-before-UGC, community guidelines and an enforced age gate each discharge an Egyptian obligation **and** unblock distribution. These should be sequenced first.

---

# PART 30 — Complete Data Inventory

Built from code evidence only. Storage location is the **Frankfurt VPS** unless otherwise stated. "Access: Admin+Coach" reflects `AdminController.cs:17`.

| Data | Source | Purpose | Sensitive? | Storage | Retention | Access | Third Party | Legal Risk |
|---|---|---|---|---|---|---|---|---|
| First/last name | Registration | Identity, display | No | SQL `AspNetUsers` | **Indefinite** | User, other users, Admin+Coach | — | MEDIUM |
| Email | Registration | Login, OTP, notices | No | SQL | **Indefinite** | User, Admin+Coach | **Google (SMTP)** | **HIGH** (transfer) |
| Password hash | Registration | Authentication | No | SQL | Life of account | None (hashed) | — | LOW |
| **Age (int)** | Registration | Unclear — unused for gating | **YES if minor** | SQL | Indefinite | User, Admin+Coach | — | **CRITICAL** (no age gate) |
| **MBTI** | `SubmitMbti` | Onboarding | **Arguably YES** (psychological) | SQL | Indefinite | User, others, Admin+Coach | — | **HIGH** |
| Bio | Profile update | Display | No | SQL (**unbounded length**) | Indefinite | Public | — | LOW |
| **Profile photo** | Registration (**required**) | Display | No (unless used for matching) | **MinIO, public URL** | **Indefinite — survives deletion** | **Likely anyone with URL** | — | **HIGH** |
| **Voice verification recording** | Registration (**required**) | Eligibility verification | **YES — biometric** | **MinIO, public URL, plain HTTP** | **Indefinite — survives deletion** | **Admin+Coach; likely anyone with URL** | — | **CRITICAL** |
| `UserStatus` | Admin review | Access control | No | SQL | Indefinite | Admin+Coach | — | LOW |
| `RefreshToken` | Login/refresh | Session | No | **SQL, plaintext** | 7 days | None by design | — | **HIGH** |
| `FcmToken` | `UpdateFcmToken` | Push | No | SQL | Until revoked/ban | Admin+Coach | **Google** | **HIGH** (transfer) |
| **Message content** | Chat / in-room private | Messaging | Content-dependent | SQL, **plaintext** | Until either party deletes | Sender, receiver, **Admin via DB** | **Google (push body)** | **HIGH** |
| Room title/description | Room creation | Discovery | No | SQL | Indefinite | Public | — | MEDIUM (unmoderated) |
| Room image | Room creation | Display | No | MinIO, public URL | Indefinite | Public | — | MEDIUM |
| **Live audio** | WebRTC | Conversation | **YES — voice** | **Not stored**; relayed | Transient | Room participants | — | **HIGH** (transfer, disclosure) |
| `TotalSpokenSeconds` | `ToggleMic` | Analytics | No | SQL | Indefinite; **blocks deletion** | Admin+Coach | — | MEDIUM |
| Room participation | Join | Room state | Inference risk (**`MentalHealth`**) | SQL | Indefinite; **blocks deletion** | Admin+Coach | — | **HIGH** |
| Friend graph | Friend requests | Social | No | SQL | Deleted on account deletion | Both users | — | LOW |
| **Device ID + metadata** | `X-Device-*` on every login/refresh | Ban-evasion prevention | No | SQL | Cascade-deleted with user | Admin+Coach | — | MEDIUM |
| Device block flag | Admin ban | Safety | No | SQL | **Destroyed on user deletion** | Admin | — | MEDIUM (Part 17.3) |
| `UserBlock` | User action | Safety | No | SQL | Deleted on deletion | Users, Admin | — | LOW |
| **Report (free text + screenshot)** | User report | Moderation | **Often YES in substance** | SQL + MinIO | **Indefinite**; `ReporterId` **blocks deletion** | Admin+Coach | — | **HIGH** |
| Support ticket / chat / messages | Support | Support | Content-dependent | SQL + MinIO | **Indefinite**; `ContactEmail` **survives deletion** | Admin | — | **HIGH** |
| `Notification` rows | System | Delivery | No | SQL | Deleted on deletion | User, Admin | — | LOW |
| `UserEvent` — type, props, session, room | Server + client | Analytics | Inference risk | SQL | **180 days** | Admin+Coach | — | MEDIUM |
| **`UserEvent.IpHash`** | Request | Abuse/analytics | Pseudonymised | SQL | **180 days** | Admin+Coach | — | **LOW — well engineered** |
| `UserEvent.UserAgent` | Request | Analytics | No | SQL | 180 days | Admin+Coach | — | MEDIUM |
| Session cookie `CocorraSessionId` | Middleware | Analytics session grouping | No | Client cookie, 7 days | 7 days | — | — | **MEDIUM** (no consent) |
| `Daily*Metrics` aggregates | Aggregation service | Dashboards | **`DailyHostMetrics` keyed by `HostId`** | SQL | **Indefinite, no cleanup** | Admin+Coach | — | **MEDIUM** |
| `DailyStateSnapshot`, `DeadLetterEvent` | Pipeline | Ops | Payloads may contain event data | SQL | **Indefinite** | Admin+Coach | — | MEDIUM |
| **Raw client IP** | User device → STUN | NAT traversal | No | Not stored by Cocorra | Transient | — | **Google + Cloudflare** | **MEDIUM** (transfer) |
| Container logs (incl. identity in token audit) | App | Diagnostics | Contains identifiers | VPS disk | **~30 MB rolling** | Server operators | — | MEDIUM |
| **Backups** | — | — | Would contain everything | **UNKNOWN** | **UNKNOWN** | **UNKNOWN** | **UNKNOWN** | **HIGH (unknown)** |
| **Production secrets** | `appsettings.json` | Config | Keys to all of the above | **Git repository + GitHub** | **Entire git history** | Anyone with repo access | **GitHub (US)** | **CRITICAL** |

**Data not collected — recorded as positive findings:** gender; date of birth; phone number; national ID; location/GPS; payment or card data; contacts list; photos library beyond the uploaded avatar; raw IP addresses at rest; room-audio recordings; third-party analytics identifiers; advertising identifiers.

---

# PART 31 — Legal Risk Register

Severity reflects legal consequence × evidential strength. **Likelihood** is the likelihood of the risk materialising as an enforcement, liability or blocking event.

| ID | Risk | Legal Basis | Evidence | Severity | Likelihood | Impact | Required Action | Lawyer Required |
|---|---|---|---|---|---|---|---|---|
| **R-01** | Production credentials (DB `sa`, JWT signing key, SMTP, LiveKit, TURN, MinIO, seed-admin password) committed to git and present in history | PDPL Art. 4 security duty; possible notifiable breach; Law 175/2018 Art. 25 (unlawful disclosure) | `appsettings.json:17,20,28,33,46,62,69`; `livekit.yaml:12,101`; `.gitignore` admits it; 6 nested `publish/` copies | **CRITICAL** | **Occurring now** | Total compromise of all personal data incl. biometric; breach notification; criminal exposure | Rotate everything; purge history; move to env vars; recreate admin; audit repo access | **YES** (breach assessment) |
| **R-02** | Entire data estate (DB, object storage, media relay) processed in **Germany** without PDPC licence or data-subject consent | PDPL Art. 14 + ER 816/2025 cross-border regime; criminal under Arts. 35–40 | IP `152.239.115.176` → Frankfurt/Hostinger; `appsettings.json:17,67`; `livekit.yaml:3` | **CRITICAL** | **High** | Criminal fines to EGP 5m; order to cease transfer; forced migration | Verify location contractually; then Route A (localise) or Route B (licence + consent) — Part 16.5 | **YES** |
| **R-03** | Sensitive/biometric voice recordings processed without licence, without explicit written consent, retained indefinitely, surviving account deletion, and likely publicly URL-accessible | PDPL Art. 12 sensitive data + Art. 4; ER licence regime | `ApplicationUser.cs:15`; `RegisterDto.cs:33`; `UploadVoice.cs:60`; `AuthServices.cs:539-597` (no delete) | **CRITICAL** | **High** | EGP 500k–5m + imprisonment (reported range for sensitive data) | Delete samples post-decision; delete on account deletion; pre-signed URLs only; consent + licence | **YES** |
| **R-04** | No privacy policy, no terms, no consent mechanism of any kind | PDPL Art. 2 (consent), transparency; ER documented/auditable consent; CPL 181/2018; Apple 1.2/5.1.1; Play UGC | Repo-wide grep returns nothing | **CRITICAL** | **Certain** | No lawful basis for any processing; app-store rejection; unenforceable moderation | Draft all documents; build consent capture with version + timestamp | **YES** |
| **R-05** | Minors can register — no age gate, no DOB, no guardian consent; behavioural analytics applied to them | PDPL children's data = sensitive; ER guardian consent <15, no profiling of children; Child Law 12/1996 | `RegisterDto.cs:18-19` (no `[Range]`) | **CRITICAL** | **High** | Sensitive-data offence; child-safety exposure; app-store removal | Enforce 18+; collect DOB; audit and remove existing under-18 accounts | **YES** |
| **R-06** | Account deletion fails for most users; files never deleted; partial deletes commit before failure (data loss) | PDPL erasure right; Apple 5.1.1(v); Play User Data | `AuthServices.cs:539-597` vs `AppDbContext.cs:58-62,89-93,158-162` | **CRITICAL** | **Certain** | Rights violation; app-store blocker; live data-loss defect | Clean all `Restrict` rows; delete files; wrap in transaction; web deletion route | Advisable |
| **R-07** | No PDPC controller/processor licence, no DPO, no registers — and **no legal entity** to apply with | PDPL Art. 31/ER licensing; DPO obligation | Part 2 — no entity evidenced | **CRITICAL** | **Certain after 31 Oct 2026** | Unlicensed processing; cannot operate lawfully | Incorporate; appoint + register DPO; apply for licence | **YES** |
| **R-08** | No breach detection, register, or 72-hour notification capability; custodian liability applies | PDPL Art. 7 / ER breach regime | Part 25 | **HIGH** | **High** | Missed statutory deadline compounds any breach | Adopt the Part 25 procedure; appoint DPO; add alerting and log retention | **YES** |
| **R-09** | Women-only positioning unenforced (no gender field), while gender is in practice inferred from a voice sample with no criteria, record or appeal | CPL 181/2018 misleading claims; PDPL sensitive-data use + transparency; platform safety-claim policies | No gender field anywhere; `UserStatus` flow | **HIGH** | **High** | Misrepresentation; safety harm; rejected-applicant complaints | Implement declared eligibility + documented review + appeal, or reframe the claim | **YES** |
| **R-10** | `Coach` role can reach `AdminController` — personal data disclosed to unvetted, unbound individuals | PDPL Art. 2 (disclosure); ER personnel confidentiality requirement | `AdminController.cs:17` | **HIGH** | **High** | Unlawful disclosure; no accountability | Remove `Coach`; per-action `Admin` policy; NDAs; access logging | Advisable |
| **R-11** | Law 175/2018 "service provider" status unresolved — 180-day retention duty may apply (fine EGP 5–20m) or, if it does not, building it would be unlawful over-collection | Law 175/2018 Arts. 1, 2, 33 | Definition unobtainable from public sources | **HIGH** | **Unknown** | EGP 5–20m fine, or PDPL over-collection exposure | **Obtain the Art. 1 definition and counsel's view before building or deleting anything** | **YES — priority** |
| **R-12** | Infrastructure exposure: DB (1433) and MinIO (9000) addressed via public IP; MinIO over plain HTTP; `TrustServerCertificate=True`; app connects as `sa`; no encryption at rest; Swagger public in production | PDPL Art. 4; ER security measures | `appsettings.json:17,67`; `Program.cs:282,335,484-495` | **HIGH** | **High** | Breach of the security duty; realistic compromise path | Firewall; TLS to MinIO; least-privilege DB user; disable production Swagger; encryption at rest | Advisable |
| **R-13** | No moderation policy, no notice-and-action, no evidence preservation, no appeal, no moderator attribution; report taxonomy covers 5 of ~17 risk classes | No Egyptian safe harbour; Law 175/2018 Arts. 25–26; Apple 1.2; Play UGC | `ReportCategory.cs`; `Report.cs`; `README.md:217-219` | **HIGH** | **High** | Liability on notice; app-store blocker; user harm | Content Moderation Policy; expand taxonomy; durable `ModerationAction` log; legal hold; appeals | **YES** |
| **R-14** | Message content transmitted to Google in push bodies; OTP email via consumer Gmail; raw client IPs to Google/Cloudflare STUN — all undisclosed, unconsented, uncontracted | PDPL Arts. 2, 14; ER transfer regime | `MessagePreview.cs`; `EmailSettings`; `appsettings.json:50-52` | **HIGH** | **High** | Unlawful transfer and disclosure | Disclose; consent; DPAs; **drop external STUN (self-hosted STUN already runs)**; business email provider |Advisable |
| **R-15** | Source code copyright may vest in the individual developer, not any company; no IP assignments | Copyright Law 82/2002 | Single git author; no entity | **HIGH** | **High** | Company may not own its product; investment blocker | Written assignments from all contributors | **YES** |
| **R-16** | No backup policy evidenced — existence, location, encryption and retention unknown | PDPL Art. 4 (integrity/availability); erasure completeness | Repo-wide: no backup config | **HIGH** | **Unknown** | Unknown breach surface; cannot honour erasure | Establish, encrypt, locate, retain, document erasure propagation | Advisable |
| **R-17** | SQL Server edition/licence unverified (Developer Edition is non-production; Express caps at 10 GB) | Microsoft licensing (contract) | `appsettings.json:17`; no edition evidence | **MEDIUM–HIGH** | **Medium** | Licence breach; or capacity-driven outage | Verify edition; licence or migrate | Advisable |
| **R-18** | Reverse proxy / TLS configuration not in version control — unaudited | PDPL Art. 4 | No Caddyfile in repo | **MEDIUM–HIGH** | **Medium** | Unknown TLS posture on three subdomains | Obtain, review, place under version control | No |
| **R-19** | No admin audit log; attribution only via a droppable channel that self-deletes at 180 days | ER inspection registers; accountability | `AdminService.cs:451-461`; `appsettings.json:86` | **MEDIUM–HIGH** | **High** | Cannot evidence moderation or data access | Durable append-only `ModerationAction` + sensitive-data access log | No |
| **R-20** | Analytics session cookie and event tracking run with no consent | PDPL Art. 2; ER implied-consent limits | `SessionTrackingMiddleware.cs:25-34` | **MEDIUM** | **High** | Unlawful processing | Gate on consent; **do not enable the `Enable*` event flags before consent exists** | No |
| **R-21** | Retention undefined for nearly all categories; aggregates keyed by `HostId` retained indefinitely; `ContactEmail` survives erasure | PDPL storage limitation; ER pre-determined retention | Part 17 | **MEDIUM** | **High** | Storage-limitation breach | Data Retention Policy + implementation | Advisable |
| **R-22** | Rate limiting is a single global 100/min per IP — no stricter limit on login, OTP, reset or registration | PDPL Art. 4 | `Program.cs:404-413` | **MEDIUM** | **Medium** | Credential stuffing; OTP brute force → account takeover | Per-endpoint limits on auth paths | No |
| **R-23** | No MFA for admin accounts with access to biometric data and all messages | PDPL Art. 4; sensitive data | `Program.cs` | **MEDIUM** | **Medium** | Single credential compromise → full data access | Admin MFA | No |
| **R-24** | Mandatory profile photo and unused MBTI field breach minimisation | PDPL minimisation | `RegisterDto.cs:35`; `ApplicationUser.cs:16` | **MEDIUM** | **High** | Minimisation breach; larger breach impact | Make photo optional; delete MBTI if unused | No |
| **R-25** | No trademark registration for "Cocorra" in a first-to-file jurisdiction | Trademark Law 82/2002 | No registration evidenced | **MEDIUM** | **Medium** | Forced rebrand after launch spend | File before public launch | Advisable |
| **R-26** | Art. 25 Law 175/2018 "family values" environment — hosts/users may be prosecuted for room speech; no LEA procedure, no user warning | Law 175/2018 Arts. 25–26 | Part 8.3 | **MEDIUM–HIGH** | **Medium** | User harm; compelled disclosure; reputational and blocking risk | LEA request procedure; candid risk disclosure in Guidelines; preserve "cannot produce audio" | **YES** |
| **R-27** | Self-hosted SFU + TURN may engage NTRA licensing or Art. 64 encryption provisions | Law 10/2003 licensing; Art. 64 | `livekit.yaml:84-101` | **MEDIUM** | **Low** | Regulatory contact; possible authorisation requirement | Specialist telecoms opinion | **YES (specialist)** |
| **R-28** | Ban-evasion control self-defeating — device blocks cascade away on account deletion | Safety/integrity (not a statutory breach) | `AppDbContext.cs:200-204` | **MEDIUM** | **High** | Banned users return undetected | Split registry from blocks; retain blocks pseudonymised | Advisable |
| **R-29** | MinIO version/licence unverified (AGPL-3.0 since 2021) | AGPL-3.0 network copyleft | Not pinned in repo | **MEDIUM** | **Low** | Copyleft exposure if modified/redistributed | Confirm version and usage | Advisable |
| **R-30** | PII in production logs from self-declared "temporary" token-audit logging | PDPL minimisation; Art. 4 | `LiveKitService.cs:56-114` | **LOW–MEDIUM** | **High** | Unnecessary identifier exposure in logs | Remove the temporary logging | No |
| **R-31** | CORS falls back to allow-any-origin-with-credentials if `Cors:AllowedOrigins` is empty | PDPL Art. 4 | `Program.cs:138-143` | **LOW–MEDIUM** | **Low** | Misconfiguration becomes a cross-origin credential leak | Fail closed instead | No |
| **R-32** | Unbounded text fields (`Bio`, `Report.Description`, support message bodies) | Art. 4; minimisation | models | **LOW** | Medium | Storage abuse; oversized PII | Add length limits | No |
| **R-33** | No third-party attribution file | Apache-2.0/MIT notice terms | No `THIRD-PARTY-NOTICES` | **LOW** | Low | Technical licence non-compliance | Generate the file | No |
| **R-34** | On monetization: host/coach payouts may constitute payment intermediation requiring CBE authorisation; VAT rate classification (14% vs 10%) determines whether the EGP 500k threshold applies at all | CBE payment-services framework; VAT Law 67/2016 | No payment code today | **DEFERRED — becomes HIGH on monetization** | — | Unlicensed payment activity; VAT default | Resolve before designing revenue-share | **YES** |

---

# PART 32 — Red / Orange / Yellow / Green Assessment

## Can Cocorra legally launch and operate in Egypt on the current implementation?

**Not on the current implementation.** That is a statement about the present state of the codebase and documentation, not about the product concept — which is viable and, in its architectural choices (self-hosted media, no room recording, hashed IPs, no third-party analytics, no payment data), better positioned than many comparable platforms.

Two things make the position urgent rather than merely imperfect. First, the PDPL transition period ends **31 October / 1 November 2026** — roughly seven weeks from the date of this audit — and the licence application alone has a **90-working-day** statutory decision window, with **silence treated as rejection**. The licence cannot realistically be obtained before the deadline, and it cannot even be *applied for* without a commercial registration that does not yet exist. Second, R-01 is not a latent risk but a live exposure: the keys to the entire dataset, including biometric data, are in a git repository.

The practical consequence: **Cocorra should not conduct a public launch before the RED items are closed**, and the founders should take counsel's view immediately on whether *continuing the current private/limited operation* is itself lawful in the interim, and on whether the credential exposure already triggers breach notification.

---

## 🔴 RED — Must fix before launch

Each of these is either a legal blocker, a live exposure, or an app-store blocker. None is optional.

| ID | Item | Why RED |
|---|---|---|
| R-01 | **Rotate every committed secret; purge git history; recreate the admin account; move secrets to environment variables** | The database, JWT signing key, email, storage and media credentials are readable by anyone with repository access. Every authorisation control in the product is void while the JWT key is public. This is an active compromise, not a risk — and it may already require breach notification. |
| R-07 | **Incorporate an Egyptian legal entity; obtain commercial registration and tax card** | A hard prerequisite for the PDPC licence application (which requires the commercial register), for the CPL disclosure duty, and for any processor contract. Nothing else in the compliance chain can start without it. |
| R-04 | **Privacy Policy, Terms of Service, Community Guidelines — drafted and published; consent capture built with version + timestamp** | Without these there is no lawful basis for any processing, no contract with users, no enforceable moderation, and no possibility of app-store approval. The ER require consent to be documented and auditable at inspection. |
| R-03 | **Stop indefinite retention of voice samples; delete on verification decision; delete on account deletion; remove public-URL access; obtain explicit written consent** | Sensitive biometric data, retained forever, surviving account deletion, likely anonymously fetchable, with no consent and no licence. Reported penalty range for sensitive-data offences is EGP 500k–5m plus imprisonment. |
| R-05 | **Enforce a minimum age (18 recommended); collect DOB; audit and remove existing under-18 accounts** | Children's data is sensitive under the PDPL; guardian consent is mandatory below 15; profiling of children is restricted — and Cocorra's analytics is profiling. The interim validation fix is small and is the highest value-per-effort item in this audit. |
| R-06 | **Fix account deletion end-to-end, including file deletion and transactional integrity** | The current path irreversibly destroys the user's messages, friendships, blocks and notifications, *then* fails, *then* refers them to support. It is simultaneously a rights violation, an Apple 5.1.1(v)/Play blocker, and a live data-loss defect. |
| R-02 | **Resolve data location: verify Hostinger's contracted region, then either localise to Egypt or obtain the cross-border licence and consent** | Unauthorised transfer abroad is criminalised. This is the largest single decision in the audit and it gates the licence application (which must name destination countries and storage locations). |
| R-12 | **Close the infrastructure exposures: firewall 1433/9000, TLS to MinIO, least-privilege DB account, fix `TrustServerCertificate`, disable production Swagger** | A publicly reachable database port or public object-storage bucket converts every other finding into an actual breach. |
| R-10 | **Remove `Coach` from `AdminController`; enforce per-action `Admin` policies** | Personal data — including access paths to voice samples — is being made available to unvetted individuals bound by no confidentiality undertaking, contrary to an express ER requirement. A small change closes it. |
| R-11 | **Obtain counsel's determination on Law 175/2018 "service provider" status** | RED not because compliance is known to be required, but because **the two possible answers demand opposite engineering actions** (build 180-day retention vs. deliberately not building it). Proceeding without the answer risks either an EGP 5–20m fine or unlawful over-collection. |
| R-08 | **Appoint and register a DPO; establish the breach register and the Part 25 procedure** | The DPO is the officer statutorily charged with 72-hour notification; without one, the notification obligation cannot be discharged at all. Custodian liability means fault is not a defence. |
| R-09 | **Either implement women-only enforcement properly or reframe the positioning** | Shipping an unenforced safety claim is a misrepresentation under CPL 181/2018, an app-store safety-claim risk, and — most seriously — it induces users to disclose more than they otherwise would in a space that is not what it says. |

---

## 🟠 ORANGE — Should fix before launch

Not strict blockers, but each materially raises risk if launch proceeds without it, and each is substantially cheaper to do before launch than after.

| ID | Item | Reasoning |
|---|---|---|
| R-13 | Content Moderation Policy; expand `ReportCategory` to cover the ~17 risk classes; durable `ModerationAction` log; legal-hold; appeals process | With no safe harbour in Egypt, exposure turns on knowledge and response. Also an Apple 1.2 / Play UGC condition — arguably RED for distribution, ORANGE for legality. |
| R-14 | Disclose and contract all third-party transfers; **drop external STUN in favour of the already-running self-hosted STUN**; replace consumer Gmail with a contracted transactional email provider | The STUN change removes two international recipients of raw client IPs at near-zero cost — the cheapest cross-border reduction available. |
| R-15 | Written IP assignments from every contributor | Standard investor-diligence blocker; cheap now, expensive to retrofit once contributors disperse. |
| R-16 | Establish, encrypt, locate and document backups, including erasure propagation | An unknown backup is an unknown breach surface *and* means no erasure claim can be made truthfully. |
| R-19 | Durable append-only admin/moderation audit log and sensitive-data access log | The ER contemplate inspectable registers; today nobody can say who has listened to a voice sample. |
| R-20 | Gate analytics and the session cookie on consent; **do not enable `EnableNewEventEmission` / `EnableHighFrequencyEvents` until consent exists** | Both flags are currently `false` — the window to get this right before scale-up is open, and closing it costs nothing now. |
| R-21 | Data Retention Policy with a period per category, implemented | Only one period exists today. Also clears `ContactEmail` surviving erasure and the `HostId`-keyed aggregates. |
| R-22, R-23 | Per-endpoint rate limits on auth paths; MFA for admins | Two small changes protecting the highest-value credentials in the system. |
| R-24 | Make the profile photo optional; delete the MBTI field if unused | Pure minimisation wins that also shrink breach impact. For a women-only platform, a mandatory photograph is a safety liability as well as a legal one. |
| R-17, R-18 | Verify the SQL Server edition/licence; obtain and review the reverse-proxy configuration | Both are unknowns sitting under production. R-17 also carries an operational cliff at Express's 10 GB limit. |
| R-26 | Law-enforcement request procedure; candid risk disclosure in the Guidelines; deliberately preserve the "cannot produce room audio" property | Requests *will* arrive. Having no procedure is how disclosures become unlawful. |
| R-28 | Split the device registry from device blocks; retain blocks pseudonymised | Restores the ban-evasion control while satisfying minimisation. |
| R-30 | Remove the self-declared "temporary" token-audit logging | Identity data at `Information` level in production, left behind after an investigation. |
| R-25 | File the "Cocorra" trademark | First-to-file jurisdiction; a forced rebrand after launch spend is the expensive outcome. |

---

## 🟡 YELLOW — Can launch with documented mitigation

Acceptable to carry into launch **provided** the mitigation is written down, owned, and dated — not merely intended.

| ID | Item | Documented mitigation required |
|---|---|---|
| R-27 | NTRA licensing / Art. 64 encryption question | Record the reasoning for the "application, not telecom operator" position, note that the service is free (so "for remuneration" is not met), obtain a specialist opinion, and **re-test on monetization**. |
| R-29 | MinIO licence position | Record the version in use, that it is unmodified, and that access is via the standard S3 API only. |
| R-31 | CORS fail-open fallback | Either fix (preferred, trivial) or document that `Cors:AllowedOrigins` is always populated in production, with a startup assertion. |
| R-32 | Unbounded text fields | Add limits, or document the accepted storage/PII risk with a monitoring threshold. |
| R-33 | Third-party attribution | Generate the notices file; low urgency until a mobile app ships. |
| — | Room-audio not recorded | **Document this as a deliberate architectural commitment**, with an alert on any LiveKit egress configuration change, so the claim stays true and is defensible. |
| — | Aggregate analytics retained indefinitely | Document that aggregates are retained, and complete the work to make `DailyHostMetrics` genuinely non-identifying. |

---

## 🟢 GREEN — Acceptable as-is

Recorded deliberately: these are done well, should not be disturbed by remediation work, and several are genuine strengths worth preserving and publishing.

| Item | Why it is acceptable |
|---|---|
| **Password hashing** | ASP.NET Core Identity defaults (PBKDF2-HMAC-SHA512, 100k iterations, per-user salt) — properly done. |
| **IP pseudonymisation** | Salted hashing with a startup guard that refuses to boot without a secret salt, no fallback, and the reasoning documented in code and README. This is exemplary privacy engineering and should be cited in the licence application. |
| **180-day raw-event retention, enforced by a cleanup service** | The only implemented retention period, and it is implemented correctly. |
| **No room-audio recording** | Architecturally protective for users and for Cocorra's exposure to compelled disclosure. |
| **Self-hosted LiveKit** | No third-party processor sees user voice. (Note the Part 10 trade-off — this is the fact that complicates the telecom question, which is a price worth paying.) |
| **No payment data** | Zero PCI scope, zero payment-breach exposure. |
| **No third-party analytics, crash-reporting, ad or AI SDKs** | An unusually self-contained data estate for a consumer app. |
| **No location data, no phone number, no national ID collected** | Sound minimisation on the categories that matter most. |
| **Immediate ban enforcement** | `OnTokenValidated` re-checks lockout on every request rather than waiting for token expiry — better than most implementations. |
| **Authorisation policy design** | Verification-status-gated default policy with a narrow named policy for the verification flow is a clean design. |
| **Secrets pattern for `IpHashSalt`** | Fail-fast, no default, `.env`-based, documented — proof the team can do secrets management correctly, and the template for fixing R-01. |
| **Permissive-only dependency licences** | No GPL/AGPL/LGPL in the .NET solution. |
| **Support system** | Tickets (anonymous permitted) plus real-time chat with claim/reply/close — a real complaint channel already exists, which CPL 181/2018 requires. |
| **Notification architecture** | Persist-then-push, durable rows plus best-effort FCM. Directly reusable for policy-change notices and breach notifications. |
| **Test suite** | 496 tests passing, with the build gated on them in the Dockerfile. Not a legal control, but it materially de-risks the remediation work ahead. |

---

# PART 33 — Required Action Plan

Every task lists: reason · legal basis · engineering change · product change · legal-document change · owner · priority. Owners are roles, to be assigned to named individuals.

## Phase 1 — Before public launch (legal blockers and critical exposures)

| # | Task | Reason | Legal basis | Engineering | Product | Legal doc | Owner | Priority |
|---|---|---|---|---|---|---|---|---|
| 1.1 | **Rotate all committed secrets; purge git history; recreate seeded admin; move every secret to env vars; delete committed `publish/` trees; audit repo access** | Live compromise of all data incl. biometric | PDPL Art. 4; Law 175/2018 Art. 25 | Yes — config, CI, `IdentitySeeder` | No | No | Tech Lead | **P0 — immediate** |
| 1.2 | **Assess whether 1.1 constitutes a notifiable breach; notify if so** | 72-hour clock runs from awareness; this audit is awareness | PDPL Art. 7 / ER | No | No | Breach record | Founder + Counsel | **P0 — immediate** |
| 1.3 | **Verify Hostinger's contracted data-centre region in writing** | Everything in R-02 depends on it | PDPL Art. 14 | No | No | Provider correspondence | Tech Lead | **P0 — immediate** |
| 1.4 | **Firewall 1433 and 9000; verify the MinIO bucket policy; TLS to MinIO; least-privilege DB user; fix `TrustServerCertificate`; disable production Swagger** | Closes the realistic compromise paths | PDPL Art. 4 | Yes | No | No | Tech Lead | **P0** |
| 1.5 | **Add minimum-age validation to `RegisterDto`; audit and remove existing under-18 accounts** | Children's data is sensitive; no gate exists | PDPL children's rules; ER | Yes — small | Age gate at signup | Terms eligibility clause | Tech Lead | **P0 — highest value per effort** |
| 1.6 | **Remove `Coach` from `AdminController`; per-action `Admin` policies** | Unlawful disclosure to unbound individuals | PDPL Art. 2; ER personnel confidentiality | Yes — small | Coach capability scoped to rooms | No | Tech Lead | **P0** |
| 1.7 | **Incorporate the entity; commercial registration; tax card** | Prerequisite for licence, CPL disclosure and contracts | ER licence application requirements; CPL 181/2018 | No | No | Incorporation docs | Founder + Corporate Lawyer | **P0 — gates 1.12** |
| 1.8 | **Obtain counsel's determination on Law 175/2018 service-provider status** | Opposite answers demand opposite engineering | Law 175/2018 Arts. 1, 2, 33 | Blocks design | No | Legal memo | Counsel | **P0 — gates 2.4** |
| 1.9 | **Appoint a DPO in writing; arrange training; establish the breach register; adopt the Part 25 procedure** | Notification is impossible without a DPO | PDPL/ER DPO + breach regime | Register tooling | No | Breach Response Policy; DPO appointment | Founder | **P1** |
| 1.10 | **Draft and publish Privacy Policy, Terms of Service, Community Guidelines** | No lawful basis, no contract, no enforceable rules | PDPL Art. 2 + transparency; CPL 181/2018; Apple 1.2/5.1.1; Play UGC | Hosting + in-app links | Policy screens | All three documents | Counsel + Founder | **P1** |
| 1.11 | **Build consent capture: clickwrap acceptance, version + hash + timestamp, separate optional consents, withdrawal, server-side enforcement** | ER require documented, auditable, inspectable consent | PDPL Art. 2; ER consent standard | Yes — `UserConsent`, `PolicyVersion` | Registration flow | Consent text | Tech Lead | **P1** |
| 1.12 | **Apply for the PDPC controller/processor licence, the sensitive-data permit, and the cross-border authorisation** | Unlicensed processing after 31 Oct 2026 | PDPL Art. 31 / ER licensing | Supply technical detail | No | Application pack | Counsel + DPO | **P1 — 90 working-day decision window; silence = rejection** |
| 1.13 | **Voice samples: delete on verification decision; delete on account deletion; pre-signed admin-only URLs; explicit written consent** | Highest-severity data category | PDPL Art. 12 + Art. 4 | Yes | Consent copy at signup | Policy disclosure | Tech Lead | **P1** |
| 1.14 | **Fix account deletion: clear all `Restrict` rows, delete files, wrap in a transaction, add an admin workflow and a web deletion route** | Rights violation + live data-loss defect + store blocker | PDPL erasure; Apple 5.1.1(v); Play | Yes | Deletion UX | Policy disclosure | Tech Lead | **P1** |
| 1.15 | **Decide and execute the data-location strategy (localise vs licence)** | Criminalised transfer | PDPL Art. 14 / ER | Migration or licence evidence | No | Licence application | Founder + Counsel | **P1** |
| 1.16 | **Resolve the women-only position: implement declared eligibility + documented, logged, appealable review — or reframe the marketing** | Unenforced safety claim | CPL 181/2018; PDPL transparency | Yes — eligibility field | Signup + review flow | Terms eligibility; policy disclosure | Founder + Counsel | **P1** |
| 1.17 | **Expand `ReportCategory`; add a priority queue for minors/threats; durable `ModerationAction` log; legal-hold flag; appeals route reachable by banned users** | Liability turns on knowledge and response | No safe harbour; Law 175/2018; Apple 1.2; Play UGC | Yes | Report + appeal UX | Content Moderation Policy | Tech Lead + Ops | **P1** |
| 1.18 | **Written IP assignments from all contributors** | Company may not own its code | Copyright Law 82/2002 | No | No | Assignment deeds | Founder + Counsel | **P1** |
| 1.19 | **NDAs and data-access agreements for every admin, coach and contractor** | Express ER requirement | ER personnel confidentiality | No | Onboarding step | NDA; Data Access Agreement; Moderator/Coach Agreement | Founder + Counsel | **P1** |

## Phase 2 — Within 30 days of launch

| # | Task | Reason | Legal basis | Engineering | Product | Legal doc | Owner | Priority |
|---|---|---|---|---|---|---|---|---|
| 2.1 | Data Retention Policy per category, implemented; clear `ContactEmail` on erasure; de-identify `HostId`-keyed aggregates; strip `IpHash`/`UserAgent`/`SessionId` from orphaned events | Storage limitation | PDPL; ER pre-determined retention | Yes | No | Data Retention Policy | DPO + Tech Lead | P2 |
| 2.2 | Rights-request tooling: `GET /api/Privacy/my-data` export; consent centre; rights-request register; identity verification; out-of-band channel for banned users | Rights are largely inoperable | PDPL rights; ER registers | Yes | Privacy settings screen | Rights procedure | Tech Lead | P2 |
| 2.3 | Third-party governance: Hostinger DPA (or migration evidence); Google/Firebase terms under the correct entity; **drop external STUN**; contracted transactional email | Undisclosed, uncontracted transfers | PDPL Arts. 2, 14; ER | Yes — small | No | DPAs | DPO | P2 |
| 2.4 | Implement the logging decided by 1.8 — authentication log; sensitive-data access log; extended log retention on a mounted volume; remove temporary token-audit logging | Security accountability, proportionate | PDPL Art. 4; ER registers | Yes | No | Logging note in policy | Tech Lead | P2 |
| 2.5 | Backups: establish, encrypt, locate, retain, document erasure propagation, test restore | Unknown surface; erasure integrity | PDPL Art. 4 | Yes | No | Retention Policy section | Tech Lead | P2 |
| 2.6 | Per-endpoint rate limits on auth paths; admin MFA; encryption at rest for DB and MinIO; HSTS; CORS fail-closed | Security duty | PDPL Art. 4; ER | Yes | No | No | Tech Lead | P2 |
| 2.7 | Minimisation: profile photo optional; delete MBTI if unused; bound `Bio`/`Report.Description`/support bodies | Minimisation | PDPL | Yes | Signup change | Policy update | Tech Lead | P2 |
| 2.8 | Split device registry from device blocks; retain blocks pseudonymised with a defined period | Restores ban-evasion control lawfully | PDPL minimisation + legitimate interest | Yes | No | Retention Policy | Tech Lead + DPO | P2 |
| 2.9 | Law-enforcement request procedure; single intake point; verification; minimum-necessary disclosure; request log | Requests will arrive | Law 175/2018; PDPL | Log tooling | No | LEA Request Procedure | DPO + Counsel | P2 |
| 2.10 | Breach detection: alerting on failed-login spikes, admin actions, storage/DB anomalies; security contact address; bulk affected-cohort notification capability | Cannot currently detect or notify | PDPL Art. 7 / ER | Yes | No | — | Tech Lead | P2 |
| 2.11 | Verify SQL Server edition and licence; obtain and version-control the reverse-proxy configuration | Unknowns under production | Microsoft licensing; PDPL Art. 4 | Possibly migration | No | Licence records | Tech Lead | P2 |
| 2.12 | Crisis/self-harm escalation path with signposting and moderator guidance | `MentalHealth` rooms will surface crises | Duty of care; child-protection edges | Routing | Crisis resources in-app | Child Safety / Crisis Policy | Ops + Counsel | P2 |
| 2.13 | File the "Cocorra" trademark in the relevant classes | First-to-file | Trademark Law 82/2002 | No | No | Filing | Founder + IP Counsel | P2 |

## Phase 3 — Within 90 days

| # | Task | Reason | Legal basis | Engineering | Product | Legal doc | Owner | Priority |
|---|---|---|---|---|---|---|---|---|
| 3.1 | Records of processing: full processing register, lawful-basis register, consent register, transfer register — structured for PDPC inspection | ER inspectable registers | ER record-keeping | Tooling | No | Register documents | DPO | P3 |
| 3.2 | DPIA-equivalent assessments for voice verification, analytics, and moderation | High-risk processing | ER risk-based approach | No | No | Assessment reports | DPO + Counsel | P3 |
| 3.3 | Moderator training, criteria handbook, decision-quality review, moderation SLA and metrics | Consistency and defensibility | Platform policies; liability posture | Metrics | No | Moderator Handbook | Ops | P3 |
| 3.4 | Third-party security review / penetration test (explicitly out of scope for this audit) | Independent assurance of Art. 4 compliance | PDPL Art. 4 | Remediation | No | Test report | Tech Lead | P3 |
| 3.5 | Network segmentation: separate DB and object storage from the application host; private networking | Defence in depth | PDPL Art. 4 | Yes | No | Architecture note | Tech Lead | P3 |
| 3.6 | Generate `THIRD-PARTY-NOTICES`; confirm MinIO version/licence; document the LiveKit egress-change alert | Licence hygiene; protects the no-recording claim | Apache-2.0/MIT; AGPL check | Yes — small | Licences screen | Notices file | Tech Lead | P3 |
| 3.7 | Audit the Flutter mobile repository — IP, licences, permissions, purpose strings, SDKs, data collection | Out of scope here; must not stay unexamined | PDPL; platform policies | Yes | No | Audit report | Tech Lead | P3 |
| 3.8 | Monetization readiness pack (Part 13 checklist), including the CBE payment-intermediation and VAT-classification questions | Avoid retrofitting under revenue pressure | CPL 181/2018; VAT Law 67/2016; CBE framework | Design | Pricing/billing | Subscription Terms; Refund Policy | Founder + Counsel + Accountant | P3 |
| 3.9 | Annual compliance review cycle; policy version control; post-incident review discipline | Sustained compliance | ER demonstrable governance | No | No | Governance calendar | DPO | P3 |

---

# PART 34 — Documents Cocorra Needs

Only documents actually warranted by the findings. "Why" ties each to evidence in this audit.

## Required before public launch

| Document | Why | Basis |
|---|---|---|
| **Privacy Policy** | None exists; nothing about the processing is disclosed | **Egyptian legal requirement** (PDPL transparency); also Apple/Play |
| **Terms of Service** | No contract with users; bans and content hosting have no contractual basis | **Egyptian legal requirement** (CPL 181/2018) + contractual necessity |
| **Community Guidelines** (incorporated by reference into the Terms) | Nothing defines objectionable conduct; moderation rests on nothing written | **Platform requirement** (Apple 1.2, Play UGC) + contractual necessity |
| **Consent notices and records design** | ER require documented, auditable, inspectable consent, with Centre-approved mechanisms | **Egyptian legal requirement** |
| **DPO appointment letter** | Statutory officer; prerequisite for breach notification and licence registration | **Egyptian legal requirement** |
| **Data Retention Policy** | Only one period exists product-wide | **Egyptian legal requirement** (ER pre-determined retention) |
| **Data Breach Response Policy** | No capability to meet 72 hours / 3 working days | **Egyptian legal requirement** |
| **Content Moderation Policy** | No criteria, no attribution, no appeal, no preservation | **Best practice + platform requirement**; materially affects liability posture |
| **Law Enforcement Request Procedure** | Requests will arrive in this jurisdiction; none exists | **Best practice**, strongly advised — see Part 8.3 |
| **User Complaint / Rights Request Procedure** | No intake, no verification, no SLA, no register | **Egyptian legal requirement** (ER registers; CPL complaint handling) |
| **Employee/Contractor NDA** | ER require all personnel bound to confidentiality | **Egyptian legal requirement** |
| **Data Access Agreement** (admins/moderators) | Sensitive-data access with nothing on file | **Egyptian legal requirement** |
| **Moderator Agreement** | Moderation authority exercised with no defined limits | **Contractual requirement** |
| **Coach/Host Agreement** | `Coach` is a privileged platform role held by external individuals | **Contractual requirement** |
| **IP Assignment Deeds** | Code copyright may vest in individuals | **Contractual requirement**; investor blocker |
| **Data Processing Agreement — Hostinger** | Primary processor; nothing on file | **Egyptian legal requirement** |
| **Child Safety Policy** | Minors can register; crisis and exploitation paths undefined | **Egyptian legal requirement** if any user may be under 18; **best practice** regardless |

## Required if/when applicable

| Document | Trigger |
|---|---|
| **Subscription Terms** | Any paid feature — CPL 181/2018 distance-contract disclosures |
| **Refund / Cancellation Policy** | Any paid feature — 14-day withdrawal; 7-day refund from contracting for services |
| **Payment processor DPA** | Payment integration |
| **Cross-border transfer licence documentation** | While any data is processed outside Egypt |
| **DPIA-equivalent assessments** | Voice verification, analytics, moderation |
| **Marketing/E-marketing consent records and dedicated licence** | Any direct electronic marketing — the ER treat this as high-risk requiring a **standalone licence** |

## Not required

| Document | Why not |
|---|---|
| **Cookie Policy** (standalone) | Only one first-party cookie (`CocorraSessionId`), no third-party trackers, no ad tech. Cover it in the Privacy Policy — a separate document would be theatre |
| **Processor-side DPAs offered to customers** | Cocorra is not a processor for anyone |
| **PCI-DSS documentation** | No payment data — preserve this |
| **AI transparency documentation** | No AI in the product |

---

# PART 35 — Source Quality

Sources are ranked per the brief's priority order. **Where a conclusion rests on secondary sources, this is stated.**

**Tier 1–7 — official / governmental**
- [Egyptian Tax Authority — VAT guidelines for digital and remote services](https://eta.gov.eg/en/content/egyptian-tax-authority-eta-has-recently-published-value-added-tax-vat-guidelines-digital)
- [Egyptian Tax Authority — simplified VAT compliance for non-resident vendors and EDPs](https://eta.gov.eg/en/content/digital-service-gd)
- [NTRA — Telecommunication Regulation Law No. 10 of 2003](https://www.tra.gov.eg/en/rules/law-no-10-of-2003/)
- [ITIDA — Executive Regulations of the E-Signature Law](https://www.itida.gov.eg/English/Documents/4.pdf)
- [GAFI — company incorporation procedures](https://www.gafi.gov.eg/English/Documents/Companies%20Incorporation%20Procedures.pdf)
- [WIPO Lex — Law No. 181 of 2018 on Consumer Protection](https://www.wipo.int/wipolex/en/legislation/details/19866)
- [WIPO Lex — MCIT Decree No. 109 of 2005 (E-Signature Executive Directive / ITIDA)](https://www.wipo.int/wipolex/en/legislation/details/13700)
- [Law No. 181 of 2018 — full text (PDF)](https://www.africa-laws.org/Egypt/Consumer%20Law/Law%20No.%20181%20of%202018%20on%20Consumer%20Protection.pdf)
- [Library of Congress — ratification of the Anti-Cybercrime Law](https://www.loc.gov/item/global-legal-monitor/2018-10-05/egypt-president-ratifies-anti-cybercrime-law/)
- [UN ESCWA — consumer protection in Egypt](https://www.unescwa.org/sites/default/files/inline-files/ABLF-2023-consumer-CP-Egypt-english.pdf)

**Tier 8 — established legal databases and practice guides**
- [Chambers and Partners — Data Protection & Privacy 2026: Egypt](https://practiceguides.chambers.com/practice-guides/data-protection-privacy-2026/egypt)
- [DLA Piper — Data Protection Laws of the World: Egypt](https://www.dlapiperdataprotection.com/?t=law&c=EG)
- [Lexology — The Egyptian Personal Data Protection Regime ahead of 31 October 2026](https://www.lexology.com/library/detail.aspx?g=334b6157-bbeb-474d-ba67-11140e89cc07)
- [Lexology — In brief: telecoms regulation in Egypt](https://www.lexology.com/library/detail.aspx?g=85c424f1-84bb-4288-8d48-df69c913cbc9)
- [Lexology — Legal framework of e-commerce business in Egypt](https://www.lexology.com/library/detail.aspx?g=0f6bc608-2b26-4d33-a646-ada72e0f980b)

**Tier 9 — reputable law firms**
- [Al Tamimi & Company — From Policy to Practice: Egypt Issues Executive Regulations of the PDPL](https://www.tamimi.com/law_update_articles/from-policy-to-practice-egypt-issues-executive-regulations-of-the-personal-data-protection-law/)
- [Legal500 — Overview of the Executive Regulations of the Egyptian PDPL](https://www.legal500.com/developments/thought-leadership/overview-of-the-executive-regulations-of-the-egyptian-personal-data-protection-law/)
- [Clyde & Co — Egypt regulatory update on data privacy](https://www.clydeco.com/en/insights/2026/01/egypt-regulatory-update-on-data-privacy)
- [Baker McKenzie — Egypt: Important Data Protection Update](https://www.bakermckenzie.com/en/insight/publications/2026/01/egypt-important-data-protection-update)
- [Access Partnership — Egypt finalises Executive Regulations to the PDPL](https://accesspartnership.com/opinion/egypt-finalises-executive-regulations-to-the-personal-data-protection-law-pdpl/)
- [Global Compliance News (Baker McKenzie) — Egypt issues new data protection law](https://www.globalcompliancenews.com/2020/11/08/egypt-and-united-arab-emirates-egypt-issues-new-data-protection-law28092020/)
- [Shalakany — New Consumer Protection Law Executive Regulations](https://shalakany.com/new-consumer-protection-law-executive-regulations/)
- [Mondaq — Electronic signatures in Egypt: legal validity under Law 15 of 2004](https://www.mondaq.com/contracts-and-commercial-law/1737494/electronic-signatures-in-egypt-%7C-legal-validity-under-law-15-of-2004)
- [Andersen Egypt — Tax rules for digital services provided in Egypt](https://eg.andersen.com/tax-digital-services-in-egypt/)
- [Shand & Partners — Executive Regulations and establishment of the Data Protection Centre](https://www.shandpartners.com/insights/briefings/telecoms-media-technology/the-issuance-of-the-executive-regulations-of-the-data-protection-law-and-the-establishment-of-the-data-protection-centre/)

**Platform policies (contractual, not law)**
- [Apple App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/) · [Apple — account deletion requirement](https://developer.apple.com/news/?id=12m75xbj)
- [Google Play — account deletion requirements](https://support.google.com/googleplay/android-developer/answer/13327111) · [Google Play — User Generated Content](https://support.google.com/googleplay/android-developer/answer/9876937) · [Google Play — Developer Program Policy](https://support.google.com/googleplay/android-developer/answer/16933379)

**Context sources on Art. 25 enforcement** (civil-society and academic — used **only** for the factual pattern of prosecutions in Part 8.3, not for any legal conclusion)
- [TIMEP — Egypt's TikTok crackdown and "family values"](https://timep.org/2020/08/13/egypts-tiktok-crackdown-and-family-values/) · [TIMEP — Cybercrime Law brief](https://timep.org/2018/12/19/cybercrime-law-brief/)
- [Columbia Global Freedom of Expression / SMEX — The TikTok Case](https://globalfreedomofexpression.columbia.edu/publications/the-tiktok-case-a-new-platform-to-oppress-women-in-egypt/)
- [EIPR — Crackdown on content creators](https://eipr.org/en/press/2025/08/crackdown-content-creators-mix-security-repression-class-discrimination-and-%E2%80%9Cmoral)
- [The Conversation — TikTok in Egypt](https://theconversation.com/tiktok-in-egypt-where-rich-and-poor-meet-and-the-state-watches-everything-253278)
- [Privacy International — State of Privacy: Egypt](https://privacyinternational.org/state-privacy/1001/state-privacy-egypt)

## Stated limitations of this research

Recorded so that counsel knows exactly where to verify rather than re-derive:

1. **No primary Arabic text was consulted.** All statutory content is from English translations and professional commentary. **Article numbers differ between translations** and must be re-verified.
2. **Law 175/2018 and Law 151/2020 full texts could not be extracted** — the available PDFs are image-only scans. The Art. 1 definition of "service provider" (R-11) is therefore **unread**, which is why that risk is flagged as a priority question rather than assessed.
3. **The Executive Regulations 816/2025 text itself was not obtained** — findings on the ER rest on professional summaries, which are consistent with one another but are secondary.
4. **No PDPC guidance, fee schedule, or portal documentation was obtained directly from the Centre.** Fee figures come from firm commentary and differ slightly between sources.
5. **Server location rests on IP geolocation**, which is strong evidence but not contractual proof.
6. **MinIO bucket policy, firewall rules, reverse-proxy configuration, SQL Server edition, and backup arrangements are all outside the repository** and could not be verified. Several findings are therefore marked *verify*.
7. **The Flutter mobile application was not available** and is unaudited.
8. Sources such as `recordinglaw.com` and `tenintel.com` appeared in search results but were **not relied upon**.

---

# PART 36 — Lawyer Review Checklist

A consolidated worklist for the engagement. Ordered by urgency.

| # | Item | Specialism | Blocking |
|---|---|---|---|
| 1 | Assess whether the committed-credentials exposure is a notifiable breach; if so, notify within 72 hours of awareness | Data protection | **Immediate** |
| 2 | Advise whether continuing current operation is lawful pending remediation | Data protection | **Immediate** |
| 3 | Determine "service provider" status under Law 175/2018 Art. 1 and the applicability of the Art. 2 retention duty | Cybercrime / telecoms | **Blocks logging design** |
| 4 | Advise on the PDPC licence and permit strategy; prepare and file the applications | Data protection | **Blocks lawful processing after 31 Oct 2026** |
| 5 | Advise on the cross-border strategy — localise vs licence; confirm whether a biometric-data transfer licence is realistically obtainable | Data protection | **Blocks R-02 decision** |
| 6 | Incorporation, commercial registration, tax card | Corporate | **Blocks item 4** |
| 7 | Draft Privacy Policy, Terms of Service, Community Guidelines | Data protection / commercial | **Blocks launch** |
| 8 | Advise on the minimum age and the guardian-consent regime; confirm Child Law duties and any mandatory reporting | Data protection / child protection | **Blocks launch** |
| 9 | Confirm whether voice verification is "biometric" for Egyptian regulatory purposes | Data protection | Drives R-03 scope |
| 10 | Advise on the lawfulness of the women-only model and of voice-based eligibility assessment | Constitutional / commercial | Drives R-09 |
| 11 | Advise on platform liability for user content; design the notice-and-action process | Cybercrime / media | Drives R-13 |
| 12 | Draft the Law Enforcement Request Procedure | Criminal / data protection | Advised pre-launch |
| 13 | NTRA / Art. 64 opinion on the self-hosted SFU and TURN | Telecoms specialist | Yellow-item mitigation |
| 14 | IP assignments; trademark filing strategy | IP | Pre-investment |
| 15 | NDAs, Data Access, Moderator and Coach agreements | Employment / commercial | Pre-onboarding |
| 16 | Confirm statutory retention periods and the data-subject response deadline | Data protection | Completes the Retention Policy |
| 17 | Advise on the erasure-vs-evidence and erasure-vs-counterparty conflicts | Data protection | Completes R-06 |
| 18 | Host/coach employment classification and withholding | Employment / tax | Before any revenue share |
| 19 | VAT rate classification (14% vs 10%) and e-invoicing applicability | Tax accountant | Before monetization |
| 20 | Whether host payouts constitute payment intermediation requiring CBE authorisation | Financial services | Before monetization |
| 21 | Whether "coaching" on mental-health topics engages regulated-practice rules | Regulatory | Advised pre-launch |

---

# Questions for an Egyptian Lawyer

Precise, answerable questions. Each is tied to the finding that prompted it.

**Data protection — licensing and registration**
1. Based on Cocorra's processing (accounts, voice verification samples, live audio relay, direct messages, moderation records, behavioural analytics), which PDPC **licence category** applies, and which additional **permits** are required — specifically for sensitive-data processing and for cross-border transfer? *(R-07, R-03, R-02)*
2. Given the **90-working-day** decision window and the **31 October 2026** deadline, what is the realistic path? If the licence cannot be granted in time, what is the lawful position for a platform that has applied but not yet been licensed — and does that differ for continuing existing operation versus launching publicly? *(R-07)*
3. Can a licence application be filed before incorporation is complete, or is the commercial register extract an absolute prerequisite? *(R-07, Part 2)*
4. What is the expected licence fee for Cocorra's likely record volume, and how is "records" counted for a platform storing users, messages and events? *(Part 16.5)*

**Data protection — sensitive data and voice**
5. Is a voice recording collected at registration and reviewed by a human to decide account eligibility **"biometric data"** and therefore sensitive personal data under PDPL Art. 1? Does the answer change if no algorithmic matching is performed? *(R-03)*
6. Does the `MentalHealth` room category, or a stored MBTI type, constitute **psychological or mental-health data** as sensitive personal data — either directly or by inference from participation records? *(Part 3.3(7), (15))*
7. What form must **explicit written consent** for sensitive-data processing take in a mobile app, and must the consent mechanism be pre-approved by the Centre before use? *(R-04)*

**Data protection — cross-border transfer**
8. Does hosting the database, object storage and media relay with a provider in **Germany** create a cross-border transfer requiring a PDPC licence and data-subject consent, and does Germany's GDPR status satisfy the adequacy limb? *(R-02)*
9. Do **Firebase Cloud Messaging** (which carries message text), **Gmail SMTP** (which carries OTP codes), and **Google/Cloudflare STUN** (which receive users' raw IP addresses directly from their devices) each constitute separate transfers requiring separate authorisation, and must each destination be named in the licence? *(R-14)*
10. Is real-time relay of live audio through a server outside Egypt a "transfer" even though nothing is persisted? *(Part 5.2(c))*

**Data protection — consent, rights and retention**
11. What is the **statutory deadline** for responding to a data-subject request, and what register must be maintained? *(Part 18.2)*
12. Are there **legal retention requirements** for security, device, IP-hash or moderation records — and if Law 175/2018 Art. 2 applies, how is a 180-day identification duty reconciled with PDPL minimisation and with irreversible IP hashing? *(R-11, R-21)*
13. **What should happen to data after account deletion?** Specifically: may moderation records and device blocks be retained beyond deletion in pseudonymised form, and for how long? Must a report filed *by* a deleted user be destroyed, and may consent/terms-acceptance records be retained as evidence of contract? *(R-06, R-28, Part 23.3)*
14. When one user deletes their account, may Cocorra delete the message history from the **other** party's view, or does the counterparty have a competing interest? *(Part 23.3)*

**Cybercrime and content**
15. Is Cocorra a **"service provider"** within Art. 1 of Law 175/2018, such that the Art. 2 180-day retention duty and the Art. 33 penalty (EGP 5–20m) apply? *(R-11 — the single most consequential open question)*
16. What is Cocorra's exposure under Arts. 25 and 26 for content spoken by users in its rooms, and does any takedown or notification duty arise once it has notice? *(R-13, R-26)*
17. **What records should be preserved for law-enforcement requests**, what verification should be required before disclosing, and may Cocorra notify the affected user? *(Part 8.4, R-26)*
18. **What obligations apply if users record other users** without consent and redistribute the recording — and what must Cocorra do on being notified? *(Part 5.2(d))*
19. Is there any **mandatory reporting duty** for suspected child exploitation, credible threats to life, or terrorism-related content discovered through moderation? *(Part 7.2, Part 8.4)*

**Product model**
20. **Does Cocorra's women-only access model create any legal issue** in Egypt, and does a rejected male applicant have any cause of action? *(R-09)*
21. Is it lawful to assess eligibility by having an administrator infer gender from a voice recording, and what procedural safeguards (criteria, records, appeal) should accompany it? *(R-09)*
22. **What minimum age should Cocorra enforce**, and what specifically changes if users aged 15–17 are admitted with their own written consent — particularly the restriction on profiling and behavioural monitoring of children? *(R-05)*
23. Does describing hosts as **"coaches"** giving guidance in `MentalHealth` rooms risk engaging Egyptian rules on regulated practice, and what disclaimers are advisable? *(Part 20.3(9))*

**Telecoms**
24. **Does operating a self-hosted LiveKit SFU and TURN relay require any NTRA authorisation, class licence or notification**, and does the answer change when the service becomes paid? *(R-27)*
25. Does Art. 64 of Law 10/2003 have any practical application to Cocorra's use of WebRTC's mandatory DTLS-SRTP encryption and TLS? *(R-27)*

**Corporate, contracts and tax**
26. **What corporate registrations are required** to operate Cocorra lawfully, and is the current arrangement unregistered commercial activity? *(Part 2)*
27. **What contractual documents must be in place before launch** — and specifically, does the ER requirement to bind personnel to confidentiality extend to unpaid community "coaches" who hold a privileged platform role? *(R-19, R-10)*
28. **What data-processing agreements are required**, and what must the Hostinger agreement contain to satisfy the ER? *(R-14, Part 15.2)*
29. Are the intellectual-property rights in the source code currently owned by any company, and what assignments are needed? *(R-15)*
30. **What tax and VAT obligations apply** on monetization — in particular, is a coach-led audio session a "digital service" at 14% or a "professional/consultancy service" at 10%, and does the EGP 500,000 registration threshold apply? *(Part 14.3)*
31. Would facilitating **payouts to hosts** make Cocorra a payment intermediary requiring Central Bank of Egypt authorisation? *(R-34)*

**Breach and enforcement**
32. **What breach-notification obligations apply**, who may sign the notification if no DPO is yet registered, and does the committed-credentials exposure identified in this audit trigger them? *(R-01, R-08)*
33. Given the **custodian liability** model, what evidence of technical and organisational measures would most effectively establish an "external cause" defence? *(Part 25.2)*
34. What is the PDPC's expected posture towards a startup that self-identifies gaps and files a licence application shortly before the deadline — is voluntary early engagement advisable? *(strategic)*

---

## Document control

| | |
|---|---|
| **Audit date** | 10 September 2026 |
| **Repository state** | branch `main`, commit `4d2a46f` + uncommitted working tree (room-duration and LiveKit room-creation work) |
| **Test suite at audit** | 496 passing, 0 failing |
| **Scope** | Backend repository, configuration, container and deploy definitions, repository documentation |
| **Explicitly out of scope** | Flutter mobile application (separate repository); live infrastructure (firewall, MinIO bucket policy, reverse proxy, SQL Server edition, backups); penetration testing; the Arabic primary texts of the cited statutes |
| **Changes made during the audit** | **None.** No code, database, infrastructure or production configuration was modified. This document is the only file created |
| **Status** | Draft for legal review — **not legal advice** |

