# Outlook Actionable Message backed by an Azure Function

This sample shows how to send an Outlook Actionable Message by using Microsoft Graph and collect the response in an Azure Function.

The solution contains two projects:

| Project | Purpose |
| --- | --- |
| `FeedbackSample` | .NET 8 isolated Azure Functions app that validates actionable message tokens and stores feedback in Azure Table Storage. |
| `FeedbackMailSender` | .NET 8 console app that sends the actionable email through Microsoft Graph. |

## How the sample works

1. `FeedbackMailSender` sends an email that contains an adaptive card.
2. The card posts feedback to the `SubmitFeedback` Azure Function.
3. The function validates the bearer token sent by Outlook.
4. The feedback is stored in Azure Table Storage.
5. Outlook can call `GetFeedbackStatus` to refresh the card after a response has already been submitted.

## Prerequisites

Before testing the sample, make sure you have:

- .NET 8 SDK
- Visual Studio 2022/2026 with the Azure development workload, or Azure Functions Core Tools
- An Azure subscription
- A Microsoft 365 tenant with mailboxes that can receive Outlook Actionable Messages
- An Azure Storage account for the function app
- A Microsoft Entra app registration that can call Microsoft Graph
- An Outlook Actionable Messages originator ID

## Required external setup

### 1. Register an actionable message provider

Register your sender in the [Actionable Messages developer dashboard](https://outlook.office.com/connectors/oam/publish) and copy the generated originator ID. The sample uses this value in both projects:

- `ActionableMessageOriginatorId` in the function app
- `GraphMail:OriginatorId` in the mail sender

If the originator ID in the card does not match the function configuration, submissions are rejected.

### 2. Enable EntraId authentication for the provider

You can use the same application registration for validating the actionable message tokens in the function app and for sending mail in the console app, or you can create separate registrations for each.

Complete the steps described in (Enable Microsoft Entra ID token for Actionable Messages)[https://learn.microsoft.com/en-us/outlook/actionable-messages/enable-entra-token-for-actionable-messages].  If these steps are incorrect or incomplete, you will see client errors when attempting to click the action button and no request will reach the Azure function.

### 3. Add permissions to send mail

To be able to send the actionable message, the mail sender application registration needs the `Mail.Send` permission. You can use either of these flows:

- `Delegated` - recommended for first-time testing. The console app uses device code flow and sends mail as the signed-in user. Grant delegated `Mail.Send`.
- `Application` - the console app uses client credentials and sends mail as `GraphMail:SenderUserId`. Grant application `Mail.Send` and provide a client secret.

Record these values:

- Tenant ID
- Client ID
- Client secret if you plan to use `Application` auth mode

## Azure resources to create

Create these Azure resources before deploying the function app:

1. A resource group
2. A storage account
3. An Azure Function App configured for `.NET 8 (Isolated)`
4. Application Insights (optional but recommended)

The function app stores feedback in Azure Table Storage. You can reuse the function app storage account or point `FeedbackStorageConnection` to a different storage account.

## Configure the Azure Function project

The Azure Function reads settings from `local.settings.json` when running locally and from Application Settings when deployed.

### Local development settings

Use `local.settings.json` for local debugging. A typical file looks like this:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "FeedbackStorageConnection": "UseDevelopmentStorage=true",
    "FeedbackTableName": "feedback",
    "ActionableMessageOriginatorId": "<originator-guid>",
    "ActionableMessageTenantId": "<microsoft-365-tenant-guid>",
    "ActionableMessageAudiences": "https://localhost:7071"
  }
}
```

### Azure application settings

After deployment, add these settings to the function app in Azure:

| Setting | Required | Description |
| --- | --- | --- |
| `AzureWebJobsStorage` | Yes | Storage connection used by Azure Functions runtime. |
| `FUNCTIONS_WORKER_RUNTIME` | Yes | Must be `dotnet-isolated`. |
| `FeedbackStorageConnection` | Yes | Storage connection used for Azure Table Storage feedback records. |
| `FeedbackTableName` | No | Table name for saved feedback. Default is `feedback`. |
| `ActionableMessageOriginatorId` | Yes | Originator ID that matches the adaptive card payload. |
| `ActionableMessageTenantId` | Recommended | Restricts accepted actionable message tokens to a tenant. |
| `ActionableMessageAudiences` | Optional | Explicit audience list. Useful for local testing or custom host names. |

When the function runs in Azure, the token validator can derive valid audiences from `WEBSITE_HOSTNAME`. If you use the default Azure Functions host name, you can usually leave `ActionableMessageAudiences` unset after deployment.

## Deploy the Azure Function to Azure

### Option 1: Publish from Visual Studio

1. Open the solution in Visual Studio.
2. Right-click the `FeedbackSample` project.
3. Select **Publish**.
4. Choose **Azure** > **Azure Function App (Windows)**.
5. Select an existing Function App or create a new one.
6. Finish the wizard and publish.
7. In the Azure portal, open the deployed Function App and add the application settings listed above.

### Option 2: Publish with Azure Functions Core Tools

From the repository root, run:

```powershell
func azure functionapp publish <your-function-app-name>
```

After publishing, add the required application settings in the Azure portal and restart the function app.

### Get the function key

Both HTTP-triggered functions use `AuthorizationLevel.Function`, so the adaptive card must call the endpoints with a function key.

After deployment:

1. Open the Function App in the Azure portal.
2. Open **Functions** > `SubmitFeedback`.
3. Open **Function Keys** and copy a key.
4. Use the same key for `GetFeedbackStatus`, or create/use a host key that works for both functions.

You will place this value in `GraphMail:FunctionKey`.

## Configure the mail sender project

The sender reads configuration from:

1. `FeedbackMailSender/appsettings.json`
2. `FeedbackMailSender/appsettings.{Environment}.json`
3. Environment variables

For local testing, keep the defaults in `appsettings.json` and put your real values into `FeedbackMailSender/appsettings.Development.json`.

Example:

```json
{
  "EntraId": {
    "TenantId": "<tenant-guid>",
    "ClientId": "<app-registration-client-id>",
    "ClientSecret": "<only-required-for-application-auth>",
    "AuthMode": "Delegated"
  },
  "GraphMail": {
    "Recipients": [
      "user1@contoso.com"
    ],
    "Subject": "We'd love your feedback",
    "SenderUserId": "sender@contoso.com",
    "IntroText": "This message contains an adaptive card. It only activates for the original recipients in Outlook.",
    "FeedbackSubmitUrl": "https://<your-function-app>.azurewebsites.net/api/SubmitFeedback",
    "FeedbackStatusUrl": "https://<your-function-app>.azurewebsites.net/api/GetFeedbackStatus",
    "FunctionKey": "<function-or-host-key>",
    "ActionDisplayName": "Submit feedback",
    "OriginatorId": "<originator-guid>"
  }
}
```

### Sender configuration notes

- `FeedbackSubmitUrl` and `FeedbackStatusUrl` must point to the deployed Azure Function, not `localhost`, if you want Outlook to invoke the card actions.
- `FunctionKey` is appended to both URLs as the `code` query string parameter.
- `OriginatorId` must match the value configured in the function app.
- `Recipients` should be real mailboxes that can receive actionable messages.
- `SenderUserId` is required only when `AuthMode` is `Application`.

## Send a test message

If you are using `appsettings.Development.json`, set the environment before running the sender:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project .\FeedbackMailSender\FeedbackMailSender.csproj
```

What to expect:

- In `Delegated` mode, the app starts device code authentication the first time it runs.
- After sign-in, the console app sends the email through Microsoft Graph.
- The output includes the generated card ID.

## Test the end-to-end flow

1. Open the email in Outlook.
2. Confirm that the adaptive card renders.
3. Choose a rating, optionally add a comment, and submit.
4. Verify that the card changes to the thank-you state.
5. Check Azure Table Storage for the saved feedback record.

The function writes feedback to the table named by `FeedbackTableName`.

## Local-only debugging notes

You can run the Azure Function locally for development, but Outlook cannot post back to `localhost` unless you expose it through a public HTTPS endpoint such as a tunnel or reverse proxy.

Local function debugging is still useful for:

- manual HTTP testing
- token validation diagnostics
- storage behavior verification

For a full end-to-end Outlook test, deploy the function to Azure first.

## Troubleshooting

- If the card renders but submit fails, verify `GraphMail:FunctionKey` and the deployed function URLs.
- If submit returns unauthorized, verify `ActionableMessageOriginatorId`, `GraphMail:OriginatorId`, and the tenant settings.
- If the sender cannot send mail, verify the Microsoft Graph permission model and admin consent.
- If the card does not light up in Outlook, verify that the mailbox, client, and originator registration support actionable messages.
- If feedback is not stored, verify `FeedbackStorageConnection` and confirm the storage account allows table operations.
