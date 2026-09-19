# Test sending and rendering an Organisational Actionable Message using Graph Explorer

This guide shows how to test organisation scoped Actionable Message sending and rendering.  Note that this doesn't test any actions or other AM functionality (that requires a backend service).

## Register and approve the Actionable Message

1. Create an EntraId application for the AM registration and note the application Id for use in step 3.
1. Go to [Actionable Message Provider Portal](https://outlook.cloud.microsoft/actionablemessage/oam/view) and log in with a user account that has Exchange licence assigned.
1. Create new OAM registration:
    - Entra App Id is from the application created in step 1.
    - Set scope to Organization.
    - Enter a target domain.  e.g. https://api.example.com (note this doesn't have to be a valid domain as we are only testing rendering).
    - Enter the sender address (this should be primary SMTP address of the user that will log into Graph Explorer to send the test message).
    - Tick to accept terms and conditions then click *Register Provider*.
1. If the user does not have global or Exchange administrator rights, log out of the portal and log in as an administrator.
1. Open Admin Dashboard and approve the AM.

## Send an Actionable Message using Graph Explorer

1. Open [Graph Explorer](https:?/aka.ms/ge).
1. Log in with the account configured as sender in the AM registration.
1. Set the HTTP method to **POST** and the endpoint to `https://graph.microsoft.com/v1.0/me/sendMail`.
1. Select the **Request body** tab and paste the following JSON, replacing the placeholder values:
    - `<YOUR_ORIGINATOR_ID>` — the Provider ID from the OAM registration dashboard.
    - `recipient@example.com` — the address to send the test message to.

```json
{
  "message": {
    "subject": "Actionable Message Test",
    "body": {
      "contentType": "HTML",
      "content": "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"><script type=\"application/adaptivecard+json\">{\"$schema\":\"http://adaptivecards.io/schemas/adaptive-card.json\",\"type\":\"AdaptiveCard\",\"version\":\"1.0\",\"originator\":\"<YOUR_ORIGINATOR_ID>\",\"hideOriginalBody\":true,\"body\":[{\"type\":\"TextBlock\",\"text\":\"Actionable Message Activated\",\"size\":\"Large\"}]}</script></head><body>No actionable message activated</body></html>"
    },
    "toRecipients": [
      {
        "emailAddress": {
          "address": "recipient@example.com"
        }
      }
    ]
  },
  "saveToSentItems": false
}
```

1. Click **Run query**. A `202 Accepted` response indicates the message was queued successfully.
1. Open the recipient mailbox in Outlook. If the AM renders correctly, the card body will display **Actionable Message Activated** in place of the fallback HTML text *No actionable message activated*.
