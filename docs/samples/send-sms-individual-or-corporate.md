# Sample: SMS to individual customers, email to corporate customers (Send SMS step)

A customer XML file arrives in a folder. For each customer the workflow checks the customer type: an **individual** gets an SMS saying the statement is ready and a PDF statement is generated; a **corporate** customer gets the PDF statement by email. Synthetic data only; the names, numbers and addresses are invented. This sample is a recipe to build in the designer, there is no loader script for it. Uses the Send SMS step ([ADR 0022](../adr/0022-send-sms-step.md)) and the Send email step ([ADR 0006](../adr/0006-send-email.md)); **both are real**: running it sends a real SMS and a real email.

## The XML

```xml
<?xml version="1.0" encoding="utf-8"?>
<customers>
  <customer>
    <type>individual</type>
    <number>C-1001</number>
    <name>Maria Example</name>
    <mobile>+357 99 000001</mobile>
  </customer>
  <customer>
    <type>corporate</type>
    <number>C-2001</number>
    <name>Example Trading Ltd</name>
    <email>accounts@example.org</email>
  </customer>
</customers>
```

The type is an element, not an attribute: Parse turns an attribute into a field named `@type`, which a condition's field path can't name. The mobile number keeps its `+` and spaces, so Parse keeps it as text (with "Read plain numbers as numbers" on, only plain numbers such as `35799000001` become numbers; the step would still write them out as digits). **For the first real run, replace `+357 99 000001` with your own number** (see "Setting up the SMS connection").

## The workflow

```
Folder input (*.xml) ─ Parse (XML → JSON) ─ For each customer ─ If / else (type is corporate?)
                                                                  ├─ Corporate ─ Generate PDF ─ Send email (attach the PDF)
                                                                  └─ Individual ─ Send SMS ─ Generate PDF
```

The corporate path generates the PDF first because Send email attaches the document made by the Generate step before it. The individual path sends the SMS first; the statement it announces is generated next (add an Archive step after it if the statement should be kept).

Settings of the steps:

| Step | Setting | Value |
|---|---|---|
| Folder input | File pattern | `*.xml` (an XML file reaches Parse as `{ "content": "<text>" }`) |
| Parse | Content field / Format | `content` / `xml` (no record path: the data becomes `{ "customers": { "customer": [ ... ] } }`) |
| For each | List field / Loop behaviour / Item name | `customers.customer` / each / `customer` (so `customer.mobile` is the current customer's number) |
| If / else | Condition | field `customer.type`, operator equals, compare with a value, value `corporate`; Yes path named `Corporate`, No path named `Individual` |
| Generate PDF (both paths) | Template / File name | a published statement template that uses `{{customer.name}}` and `{{customer.number}}` / `{{customer.number}}.pdf` |
| Send email | To / Subject / Attach / Email connection | `{{customer.email}}` / `Your statement {{customer.number}}` / The generated document / an email connection from Configuration, Connections (or the Worker's default relay) |
| Send SMS | SMS connection | the transactional connection set up below |
| Send SMS | Recipient's number | `{{customer.mobile}}` |
| Send SMS | Message | `Dear {{customer.name}}, your statement {{customer.number}} is ready.` |

The message box counts the template as typed (`n / 480`); up to 160 characters is one SMS, longer texts go in parts. The finished text may be at most 480 characters.

## Setting up the SMS connection (administrator)

1. **Configuration (the gear), Connections, New connection**, authentication **SMS (Brevo)**: a key such as `sms-statements`, the Brevo API key, a sender your destination countries accept (some countries pre-register alphanumeric senders: check Brevo's rules), type **transactional** (marketing connections can't be used in workflows), Unicode on if texts may hold Greek or other non-GSM characters.
2. **Test connection with your own number first.** It sends one real SMS (one credit) and shows Brevo's answer code. This also confirms that Brevo accepts Verso's send path (ADR 0022, item 9); if the test fails with `sms.client_error`, stop and report it.
3. Open the connection again, switch on **Allow workflows to send SMS** and confirm. Workflows can now send real SMS through it.
4. Type the **Allowed country prefixes**, digits without `+`: for the first run only your own country (for example `357`). A number in any other country is refused before anything is sent (the run ends Rejected `data.sms_invalid`). An empty list allows no country.
5. Recommended on the Worker: `"SmsConnections": { "AllowedHosts": [ "api.brevo.com" ] }` ([setup.md, section 10](../setup.md)).

## Things to know

- **A sent SMS cannot be recalled**, and the step is never retried automatically. If a run fails with "The outcome is unknown", the SMS may have gone: look it up in Brevo's log by the tag `verso-<first 12 hex characters of the execution id>-<item number>` before retrying (runbook C2).
- **A data problem for one customer rejects the whole run** (`data.sms_invalid`): a missing or invalid number, a missing name, a country that isn't allowed. Customers handled earlier in the loop already got their SMS or email, and those are not repeated, but the later customers are not handled. Fix the file and submit it again: **a new run sends every SMS again**, so remove the customers already handled, or send one customer per file.
- The run history shows only the Brevo message id, the number of parts, the time and the connection key for each SMS; never the number or the text. The publish audit records the number and text **templates** (`{{customer.mobile}}`, the message with its placeholders), so type a field, not a real number, in "Recipient's number".
- Each SMS costs a Brevo credit and there is no daily cap in Verso: a file with many individual customers sends many messages (at most 200 per run, the For each limit).
