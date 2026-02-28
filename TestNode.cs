using Godot;
using System;
using GitHub.Copilot.SDK;

public partial class TestNode : Node
{
    public override async void _Ready()
    {
        GD.Print("Запускаем тест Copilot SDK...");

        try
        {
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                CliUrl = "localhost:59707",
                UseStdio = false
            });

            await using var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = "gpt-4.1",
                OnPermissionRequest = PermissionHandler.ApproveAll
            });

            GD.Print("Сессия создана! Отправляем запрос...");

            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = "What is 2 + 2? Answer briefly." }
            );

            GD.Print("Ответ от Copilot: " + response?.Data.Content);
        }
        catch (Exception e)
        {
            GD.PrintErr("Ошибка: " + e.Message);
            GD.PrintErr("Тип: " + e.GetType().Name);
        }
    }
}