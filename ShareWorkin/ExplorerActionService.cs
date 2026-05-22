using System;
using System.IO;
using Microsoft.VisualBasic.FileIO;
using ShareWorkin.SMB;

namespace ShareWorkin;

public enum ExplorerActionState
{
    NoChange,
    Blocked,
    Success,
    Failure,
}

public sealed class ExplorerActionResult
{
    public ExplorerActionState State { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string UserMessage { get; init; } = string.Empty;
    public string LogMessage { get; init; } = string.Empty;
    public string? HistoryMessage { get; init; }
    public HistoryOutcome HistoryOutcome { get; init; } = HistoryOutcome.Info;
    public string? Source { get; init; } = "ExplorerActionService";
    public string? TargetName { get; init; }
    public string? PathText { get; init; }
    public string? Note { get; init; }
    public string? SourcePath { get; init; }
    public string? SourceParent { get; init; }
    public string? DestinationPath { get; init; }
    public string? DestinationFolder { get; init; }

    public bool ShouldRefreshUi => State == ExplorerActionState.Success;
    public bool ShouldWriteHistory => !string.IsNullOrWhiteSpace(HistoryMessage);
}

public sealed class MoveItemRequest
{
    public required string ModeLabel { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationFolder { get; init; }
    public required Func<string, bool> IsHoldFolderPath { get; init; }
    public required Func<string, string, bool> IsUnderFolder { get; init; }
    public required Func<string, string> GetShareStatus { get; init; }
    public required Action BeforeWrite { get; init; }
}

public sealed class RenameItemRequest
{
    public required string ModeLabel { get; init; }
    public required string SourcePath { get; init; }
    public required string NewName { get; init; }
    public required bool IsDirectory { get; init; }
    public required Func<string, string> GetShareStatus { get; init; }
    public required Action BeforeWrite { get; init; }
}

public sealed class CopyItemRequest
{
    public required string ModeLabel { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationFolder { get; init; }
    public required Func<string, bool> IsHoldFolderPath { get; init; }
    public required Func<string, string, bool> IsUnderFolder { get; init; }
    public required Func<string, string> GetShareStatus { get; init; }
    public required Action BeforeWrite { get; init; }
}

public sealed class PlaceExternalItemRequest
{
    public required string ModeLabel { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationFolder { get; init; }
    public required Action BeforeWrite { get; init; }
}

public sealed class HoldItemRequest
{
    public required string ModeLabel { get; init; }
    public required string SourcePath { get; init; }
    public required string HoldFolderPath { get; init; }
    public required Func<string, string> GetShareStatus { get; init; }
    public required Action BeforeWrite { get; init; }
}

public sealed class DeleteItemRequest
{
    public required string ModeLabel { get; init; }
    public required string ItemPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required Func<string, string> GetShareStatus { get; init; }
    public required Action BeforeWrite { get; init; }
}

public sealed class CreateFolderRequest
{
    public required string ModeLabel { get; init; }
    public required string ParentFolder { get; init; }
    public required string FolderName { get; init; }
    public required Func<string, string> GetShareStatus { get; init; }
    public required Action BeforeWrite { get; init; }
}

public static class ExplorerActionService
{
    public static ExplorerActionResult ValidateMoveTarget(MoveItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !Directory.Exists(request.DestinationFolder))
        {
            return Blocked("Move", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Move validation blocked - destination not found: {request.DestinationFolder}",
                targetName: string.IsNullOrWhiteSpace(request.SourcePath) ? null : Path.GetFileName(request.SourcePath));
        }

        if (request.IsHoldFolderPath(request.SourcePath))
        {
            return Blocked("Move", "保留は移せません。",
                $"Explorer[{request.ModeLabel}]: Move validation blocked - hold folder: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        bool sourceIsDirectory = Directory.Exists(request.SourcePath);
        bool sourceIsFile = File.Exists(request.SourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            return Blocked("Move", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Move validation blocked - source not found: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        string sourceParent = Path.GetDirectoryName(request.SourcePath) ?? string.Empty;
        if (string.Equals(
                Path.GetFullPath(sourceParent),
                Path.GetFullPath(request.DestinationFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            return Blocked("Move", "同じ場所には移せません。",
                $"Explorer[{request.ModeLabel}]: Move validation blocked - same location: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: sourceParent);
        }

        if (request.IsHoldFolderPath(request.DestinationFolder))
        {
            return Blocked("Move", "保留へは保留操作でしまってください。",
                $"Explorer[{request.ModeLabel}]: Move validation blocked - destination is hold folder: {request.DestinationFolder}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder,
                note: $"移動元: {sourceParent}");
        }

        if (sourceIsDirectory && request.IsUnderFolder(request.DestinationFolder, request.SourcePath))
        {
            return Blocked("Move", "その中へは移せません。",
                $"Explorer[{request.ModeLabel}]: Move validation blocked - destination is under source: {request.SourcePath} -> {request.DestinationFolder}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder,
                note: $"移動元: {sourceParent}");
        }

        string sourceName = Path.GetFileName(request.SourcePath);
        string destinationPath = Path.Combine(request.DestinationFolder, sourceName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Move", "同じ名前があるので移せません。",
                $"Explorer[{request.ModeLabel}]: Move validation blocked - same name: {destinationPath}",
                targetName: sourceName,
                pathText: request.DestinationFolder,
                note: $"移動元: {sourceParent}");
        }

        return new ExplorerActionResult
        {
            State = ExplorerActionState.Success,
            EventType = "Move",
            TargetName = sourceName,
            PathText = request.DestinationFolder,
            SourcePath = request.SourcePath,
            SourceParent = sourceParent,
            DestinationPath = destinationPath,
            DestinationFolder = request.DestinationFolder,
        };
    }

    public static ExplorerActionResult ValidateCopyTarget(CopyItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !Directory.Exists(request.DestinationFolder))
        {
            return Blocked("Copy", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Copy validation blocked - destination not found: {request.DestinationFolder}",
                targetName: string.IsNullOrWhiteSpace(request.SourcePath) ? null : Path.GetFileName(request.SourcePath));
        }

        if (request.IsHoldFolderPath(request.SourcePath))
        {
            return Blocked("Copy", "保留はコピーできません。",
                $"Explorer[{request.ModeLabel}]: Copy validation blocked - hold folder: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        bool sourceIsDirectory = Directory.Exists(request.SourcePath);
        bool sourceIsFile = File.Exists(request.SourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            return Blocked("Copy", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Copy validation blocked - source not found: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        string sourceParent = Path.GetDirectoryName(request.SourcePath) ?? string.Empty;
        if (string.Equals(
                Path.GetFullPath(sourceParent),
                Path.GetFullPath(request.DestinationFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            return Blocked("Copy", "同じ場所にはコピーできません。",
                $"Explorer[{request.ModeLabel}]: Copy validation blocked - same location: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: sourceParent);
        }

        if (request.IsHoldFolderPath(request.DestinationFolder))
        {
            return Blocked("Copy", "保留へは保留操作でしまってください。",
                $"Explorer[{request.ModeLabel}]: Copy validation blocked - destination is hold folder: {request.DestinationFolder}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder,
                note: $"コピー元: {sourceParent}");
        }

        if (sourceIsDirectory && request.IsUnderFolder(request.DestinationFolder, request.SourcePath))
        {
            return Blocked("Copy", "その中へはコピーできません。",
                $"Explorer[{request.ModeLabel}]: Copy validation blocked - destination is under source: {request.SourcePath} -> {request.DestinationFolder}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder,
                note: $"コピー元: {sourceParent}");
        }

        string sourceName = Path.GetFileName(request.SourcePath);
        string destinationPath = Path.Combine(request.DestinationFolder, sourceName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Copy", "同じ名前があるのでコピーできません。",
                $"Explorer[{request.ModeLabel}]: Copy validation blocked - same name: {destinationPath}",
                targetName: sourceName,
                pathText: request.DestinationFolder,
                note: $"コピー元: {sourceParent}");
        }

        return new ExplorerActionResult
        {
            State = ExplorerActionState.Success,
            EventType = "Copy",
            TargetName = sourceName,
            PathText = request.DestinationFolder,
            SourcePath = request.SourcePath,
            SourceParent = sourceParent,
            DestinationPath = destinationPath,
            DestinationFolder = request.DestinationFolder,
        };
    }

    public static ExplorerActionResult ValidatePlaceTarget(PlaceExternalItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !Directory.Exists(request.DestinationFolder))
        {
            return Blocked("Place", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Place validation blocked - destination not found: {request.DestinationFolder}",
                targetName: string.IsNullOrWhiteSpace(request.SourcePath) ? null : Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder);
        }

        bool sourceIsDirectory = Directory.Exists(request.SourcePath);
        bool sourceIsFile = File.Exists(request.SourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            return Blocked("Place", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Place validation blocked - source not found: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder);
        }

        string sourceName = Path.GetFileName(request.SourcePath);
        string destinationPath = Path.Combine(request.DestinationFolder, sourceName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Place", $"{sourceName} は同じ名前があるので置けません。",
                $"Explorer[{request.ModeLabel}]: Place validation blocked - same name: {destinationPath}",
                targetName: sourceName,
                pathText: request.DestinationFolder,
                note: $"配置元: {request.SourcePath}");
        }

        return new ExplorerActionResult
        {
            State = ExplorerActionState.Success,
            EventType = "Place",
            TargetName = sourceName,
            PathText = request.DestinationFolder,
            SourcePath = request.SourcePath,
            DestinationPath = destinationPath,
            DestinationFolder = request.DestinationFolder,
        };
    }

    public static ExplorerActionResult ValidateHoldTarget(HoldItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return Blocked("Hold", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Hold validation blocked - empty source");
        }

        bool sourceIsDirectory = Directory.Exists(request.SourcePath);
        bool sourceIsFile = File.Exists(request.SourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            return Blocked("Hold", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Hold validation blocked - source not found: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        string sourceName = Path.GetFileName(request.SourcePath);
        string sourceParent = Path.GetDirectoryName(request.SourcePath) ?? string.Empty;
        string destinationPath = Path.Combine(request.HoldFolderPath, sourceName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Hold", $"{sourceName} は同じ名前があるので保留にできません。",
                $"Explorer[{request.ModeLabel}]: Hold validation blocked - same name: {destinationPath}",
                targetName: sourceName,
                pathText: request.HoldFolderPath,
                note: $"保留前: {sourceParent}");
        }

        return new ExplorerActionResult
        {
            State = ExplorerActionState.Success,
            EventType = "Hold",
            TargetName = sourceName,
            PathText = request.HoldFolderPath,
            SourcePath = request.SourcePath,
            SourceParent = sourceParent,
            DestinationPath = destinationPath,
            DestinationFolder = request.HoldFolderPath,
        };
    }

    public static ExplorerActionResult MoveItem(MoveItemRequest request)
    {
        SwkLogger.Info($"Explorer[{request.ModeLabel}]: Move attempt: {request.SourcePath} -> {request.DestinationFolder}");
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !Directory.Exists(request.DestinationFolder))
        {
            return Blocked("Move", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Move blocked - destination not found: {request.DestinationFolder}",
                targetName: string.IsNullOrWhiteSpace(request.SourcePath) ? null : Path.GetFileName(request.SourcePath));
        }

        if (request.IsHoldFolderPath(request.SourcePath))
        {
            return Blocked("Move", "保留は移せません。",
                $"Explorer[{request.ModeLabel}]: Move blocked - hold folder: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        bool sourceIsDirectory = Directory.Exists(request.SourcePath);
        bool sourceIsFile = File.Exists(request.SourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            return Blocked("Move", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Move blocked - source not found: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        string sourceParent = Path.GetDirectoryName(request.SourcePath) ?? string.Empty;
        if (string.Equals(
                Path.GetFullPath(sourceParent),
                Path.GetFullPath(request.DestinationFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            return Blocked("Move", "同じ場所には移せません。",
                $"Explorer[{request.ModeLabel}]: Move blocked - same location: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: sourceParent);
        }

        if (request.IsHoldFolderPath(request.DestinationFolder))
        {
            return Blocked("Move", "保留へは保留操作でしまってください。",
                $"Explorer[{request.ModeLabel}]: Move blocked - destination is hold folder: {request.DestinationFolder}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder,
                note: $"移動元: {sourceParent}");
        }

        if (sourceIsDirectory && request.IsUnderFolder(request.DestinationFolder, request.SourcePath))
        {
            return Blocked("Move", "その中へは移せません。",
                $"Explorer[{request.ModeLabel}]: Move blocked - destination is under source: {request.SourcePath} -> {request.DestinationFolder}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder,
                note: $"移動元: {sourceParent}");
        }

        string sourceName = Path.GetFileName(request.SourcePath);
        string destinationPath = Path.Combine(request.DestinationFolder, sourceName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Move", "同じ名前があるので移せません。",
                $"Explorer[{request.ModeLabel}]: Move blocked - same name: {destinationPath}",
                targetName: sourceName,
                pathText: request.DestinationFolder,
                note: $"移動元: {sourceParent}");
        }

        string sourceStatus = request.GetShareStatus(request.SourcePath);
        try
        {
            request.BeforeWrite();
            if (sourceIsDirectory)
                Directory.Move(request.SourcePath, destinationPath);
            else
                File.Move(request.SourcePath, destinationPath);

            return new ExplorerActionResult
            {
                State = ExplorerActionState.Success,
                EventType = "Move",
                UserMessage = $"{sourceName} を移しました。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Move success: {sourceName}({sourceStatus}) -> {request.DestinationFolder}",
                HistoryMessage = $"{sourceName} を移しました。",
                HistoryOutcome = HistoryOutcome.Success,
                Source = "ExplorerActionService.MoveItem",
                TargetName = sourceName,
                PathText = request.DestinationFolder,
                Note = $"移動元: {sourceParent}",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
                DestinationPath = destinationPath,
                DestinationFolder = request.DestinationFolder,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.Failure,
                EventType = "Move",
                UserMessage = "移せませんでした。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Move failed: {sourceName}({sourceStatus}) -> {request.DestinationFolder}: {ex.Message}",
                HistoryMessage = $"{sourceName} を移せませんでした。",
                HistoryOutcome = HistoryOutcome.Failure,
                Source = "ExplorerActionService.MoveItem",
                TargetName = sourceName,
                PathText = request.DestinationFolder,
                Note = $"移動元: {sourceParent}",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
                DestinationPath = destinationPath,
                DestinationFolder = request.DestinationFolder,
            };
        }
    }

    public static ExplorerActionResult RenameItem(RenameItemRequest request)
    {
        SwkLogger.Info($"Explorer[{request.ModeLabel}]: Rename attempt: {request.SourcePath} -> {request.NewName}");
        string newName = request.NewName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.NoChange,
                EventType = "Rename",
                Source = "ExplorerActionService.RenameItem",
                SourcePath = request.SourcePath,
            };
        }

        string sourceParent = Path.GetDirectoryName(request.SourcePath) ?? string.Empty;
        string oldName = Path.GetFileName(request.SourcePath);
        if (string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.NoChange,
                EventType = "Rename",
                Source = "ExplorerActionService.RenameItem",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
            };
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Blocked("Rename", "その名前には変えられません。",
                $"Explorer[{request.ModeLabel}]: Rename blocked - invalid name: {oldName} -> {newName}",
                targetName: newName,
                pathText: sourceParent,
                note: $"旧名: {oldName}");
        }

        string destinationPath = Path.Combine(sourceParent, newName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Rename", "同じ名前があるので変えられません。",
                $"Explorer[{request.ModeLabel}]: Rename blocked - same name: {destinationPath}",
                targetName: newName,
                pathText: sourceParent,
                note: $"旧名: {oldName}");
        }

        string sourceStatus = request.GetShareStatus(request.SourcePath);
        try
        {
            request.BeforeWrite();
            if (request.IsDirectory)
                Directory.Move(request.SourcePath, destinationPath);
            else
                File.Move(request.SourcePath, destinationPath);

            return new ExplorerActionResult
            {
                State = ExplorerActionState.Success,
                EventType = "Rename",
                UserMessage = $"{oldName} を {newName} に変えました。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Rename success: {oldName}({sourceStatus}) -> {newName} in {sourceParent}",
                HistoryMessage = $"{oldName} を {newName} に変えました。",
                HistoryOutcome = HistoryOutcome.Success,
                Source = "ExplorerActionService.RenameItem",
                TargetName = newName,
                PathText = sourceParent,
                Note = $"旧名: {oldName}",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
                DestinationPath = destinationPath,
                DestinationFolder = sourceParent,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.Failure,
                EventType = "Rename",
                UserMessage = "名前を変えられませんでした。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Rename failed: {oldName}({sourceStatus}) -> {newName}: {ex.Message}",
                HistoryMessage = $"{oldName} を {newName} に変えられませんでした。",
                HistoryOutcome = HistoryOutcome.Failure,
                Source = "ExplorerActionService.RenameItem",
                TargetName = newName,
                PathText = sourceParent,
                Note = $"旧名: {oldName}",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
                DestinationPath = destinationPath,
                DestinationFolder = sourceParent,
            };
        }
    }

    public static ExplorerActionResult CopyItem(CopyItemRequest request)
    {
        SwkLogger.Info($"Explorer[{request.ModeLabel}]: Copy attempt: {request.SourcePath} -> {request.DestinationFolder}");
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !Directory.Exists(request.DestinationFolder))
        {
            return Blocked("Copy", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Copy blocked - destination not found: {request.DestinationFolder}",
                targetName: string.IsNullOrWhiteSpace(request.SourcePath) ? null : Path.GetFileName(request.SourcePath));
        }

        if (request.IsHoldFolderPath(request.SourcePath))
        {
            return Blocked("Copy", "保留はコピーできません。",
                $"Explorer[{request.ModeLabel}]: Copy blocked - hold folder: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        bool sourceIsDirectory = Directory.Exists(request.SourcePath);
        bool sourceIsFile = File.Exists(request.SourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            return Blocked("Copy", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Copy blocked - source not found: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        string sourceParent = Path.GetDirectoryName(request.SourcePath) ?? string.Empty;
        if (string.Equals(
                Path.GetFullPath(sourceParent),
                Path.GetFullPath(request.DestinationFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            return Blocked("Copy", "同じ場所にはコピーできません。",
                $"Explorer[{request.ModeLabel}]: Copy blocked - same location: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: sourceParent);
        }

        if (request.IsHoldFolderPath(request.DestinationFolder))
        {
            return Blocked("Copy", "保留へは保留操作でしまってください。",
                $"Explorer[{request.ModeLabel}]: Copy blocked - destination is hold folder: {request.DestinationFolder}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder,
                note: $"コピー元: {sourceParent}");
        }

        if (sourceIsDirectory && request.IsUnderFolder(request.DestinationFolder, request.SourcePath))
        {
            return Blocked("Copy", "その中へはコピーできません。",
                $"Explorer[{request.ModeLabel}]: Copy blocked - destination is under source: {request.SourcePath} -> {request.DestinationFolder}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder,
                note: $"コピー元: {sourceParent}");
        }

        string sourceName = Path.GetFileName(request.SourcePath);
        string destinationPath = Path.Combine(request.DestinationFolder, sourceName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Copy", "同じ名前があるのでコピーできません。",
                $"Explorer[{request.ModeLabel}]: Copy blocked - same name: {destinationPath}",
                targetName: sourceName,
                pathText: request.DestinationFolder,
                note: $"コピー元: {sourceParent}");
        }

        string sourceStatus = request.GetShareStatus(request.SourcePath);
        try
        {
            request.BeforeWrite();
            if (sourceIsDirectory)
                CopyDirectory(request.SourcePath, destinationPath);
            else
                File.Copy(request.SourcePath, destinationPath);

            return new ExplorerActionResult
            {
                State = ExplorerActionState.Success,
                EventType = "Copy",
                UserMessage = $"{sourceName} をコピーしました。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Copy success: {sourceName}({sourceStatus}) -> {request.DestinationFolder}",
                HistoryMessage = $"{sourceName} をコピーしました。",
                HistoryOutcome = HistoryOutcome.Success,
                Source = "ExplorerActionService.CopyItem",
                TargetName = sourceName,
                PathText = request.DestinationFolder,
                Note = $"コピー元: {sourceParent}",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
                DestinationPath = destinationPath,
                DestinationFolder = request.DestinationFolder,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.Failure,
                EventType = "Copy",
                UserMessage = "コピーできませんでした。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Copy failed: {sourceName}({sourceStatus}) -> {request.DestinationFolder}: {ex.Message}",
                HistoryMessage = $"{sourceName} をコピーできませんでした。",
                HistoryOutcome = HistoryOutcome.Failure,
                Source = "ExplorerActionService.CopyItem",
                TargetName = sourceName,
                PathText = request.DestinationFolder,
                Note = $"コピー元: {sourceParent}",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
                DestinationPath = destinationPath,
                DestinationFolder = request.DestinationFolder,
            };
        }
    }

    public static ExplorerActionResult PlaceExternalItem(PlaceExternalItemRequest request)
    {
        SwkLogger.Info($"Explorer[{request.ModeLabel}]: Place attempt: {request.SourcePath} -> {request.DestinationFolder}");
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !Directory.Exists(request.DestinationFolder))
        {
            return Blocked("Place", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Place blocked - destination not found: {request.DestinationFolder}",
                targetName: string.IsNullOrWhiteSpace(request.SourcePath) ? null : Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder);
        }

        bool sourceIsDirectory = Directory.Exists(request.SourcePath);
        bool sourceIsFile = File.Exists(request.SourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            return Blocked("Place", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Place blocked - source not found: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: request.DestinationFolder);
        }

        string sourceName = Path.GetFileName(request.SourcePath);
        string destinationPath = Path.Combine(request.DestinationFolder, sourceName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Place", $"{sourceName} は同じ名前があるので置けません。",
                $"Explorer[{request.ModeLabel}]: Place blocked - same name: {destinationPath}",
                targetName: sourceName,
                pathText: request.DestinationFolder,
                note: $"配置元: {request.SourcePath}");
        }

        try
        {
            request.BeforeWrite();
            if (sourceIsDirectory)
                CopyDirectory(request.SourcePath, destinationPath);
            else
                File.Copy(request.SourcePath, destinationPath);

            return new ExplorerActionResult
            {
                State = ExplorerActionState.Success,
                EventType = "Place",
                UserMessage = $"{sourceName} を置きました。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Place success: {sourceName}(外部) -> {request.DestinationFolder}",
                HistoryMessage = $"{sourceName} を置きました。",
                HistoryOutcome = HistoryOutcome.Success,
                Source = "ExplorerActionService.PlaceExternalItem",
                TargetName = sourceName,
                PathText = request.DestinationFolder,
                Note = $"配置元: {request.SourcePath}",
                SourcePath = request.SourcePath,
                DestinationPath = destinationPath,
                DestinationFolder = request.DestinationFolder,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.Failure,
                EventType = "Place",
                UserMessage = "置けませんでした。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Place failed: {sourceName}(外部) -> {request.DestinationFolder}: {ex.Message}",
                HistoryMessage = $"{sourceName} を置けませんでした。",
                HistoryOutcome = HistoryOutcome.Failure,
                Source = "ExplorerActionService.PlaceExternalItem",
                TargetName = sourceName,
                PathText = request.DestinationFolder,
                Note = $"配置元: {request.SourcePath}",
                SourcePath = request.SourcePath,
                DestinationPath = destinationPath,
                DestinationFolder = request.DestinationFolder,
            };
        }
    }

    public static ExplorerActionResult HoldItem(HoldItemRequest request)
    {
        SwkLogger.Info($"Explorer[{request.ModeLabel}]: Hold attempt: {request.SourcePath}");
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return Blocked("Hold", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Hold blocked - empty source");
        }

        bool sourceIsDirectory = Directory.Exists(request.SourcePath);
        bool sourceIsFile = File.Exists(request.SourcePath);
        if (!sourceIsDirectory && !sourceIsFile)
        {
            return Blocked("Hold", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Hold blocked - source not found: {request.SourcePath}",
                targetName: Path.GetFileName(request.SourcePath),
                pathText: Path.GetDirectoryName(request.SourcePath));
        }

        string sourceName = Path.GetFileName(request.SourcePath);
        string sourceParent = Path.GetDirectoryName(request.SourcePath) ?? string.Empty;
        string destinationPath = Path.Combine(request.HoldFolderPath, sourceName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return Blocked("Hold", $"{sourceName} は同じ名前があるので保留にできません。",
                $"Explorer[{request.ModeLabel}]: Hold blocked - same name: {destinationPath}",
                targetName: sourceName,
                pathText: request.HoldFolderPath,
                note: $"保留前: {sourceParent}");
        }

        string sourceStatus = request.GetShareStatus(request.SourcePath);
        try
        {
            request.BeforeWrite();
            if (sourceIsDirectory)
                Directory.Move(request.SourcePath, destinationPath);
            else
                File.Move(request.SourcePath, destinationPath);

            return new ExplorerActionResult
            {
                State = ExplorerActionState.Success,
                EventType = "Hold",
                UserMessage = $"{sourceName} を保留にしまいました。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Hold success: {sourceName}({sourceStatus}) -> {request.HoldFolderPath}",
                HistoryMessage = $"{sourceName} を保留にしまいました。",
                HistoryOutcome = HistoryOutcome.Success,
                Source = "ExplorerActionService.HoldItem",
                TargetName = sourceName,
                PathText = request.HoldFolderPath,
                Note = $"保留前: {sourceParent}",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
                DestinationPath = destinationPath,
                DestinationFolder = request.HoldFolderPath,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.Failure,
                EventType = "Hold",
                UserMessage = $"{sourceName} を保留にできませんでした。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Hold failed: {sourceName}({sourceStatus}) -> {request.HoldFolderPath}: {ex.Message}",
                HistoryMessage = $"{sourceName} を保留にできませんでした。",
                HistoryOutcome = HistoryOutcome.Failure,
                Source = "ExplorerActionService.HoldItem",
                TargetName = sourceName,
                PathText = request.HoldFolderPath,
                Note = $"保留前: {sourceParent}",
                SourcePath = request.SourcePath,
                SourceParent = sourceParent,
                DestinationPath = destinationPath,
                DestinationFolder = request.HoldFolderPath,
            };
        }
    }

    public static ExplorerActionResult DeleteItem(DeleteItemRequest request)
    {
        SwkLogger.Info($"Explorer[{request.ModeLabel}]: Delete attempt: {request.ItemPath}");
        if (string.IsNullOrWhiteSpace(request.ItemPath))
        {
            return Blocked("Delete", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Delete blocked - empty path");
        }

        bool exists = request.IsDirectory ? Directory.Exists(request.ItemPath) : File.Exists(request.ItemPath);
        if (!exists)
        {
            return Blocked("Delete", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: Delete blocked - not found: {request.ItemPath}",
                targetName: Path.GetFileName(request.ItemPath),
                pathText: Path.GetDirectoryName(request.ItemPath));
        }

        string itemName = Path.GetFileName(request.ItemPath);
        string itemParent = Path.GetDirectoryName(request.ItemPath) ?? string.Empty;
        string itemStatus = request.GetShareStatus(request.ItemPath);

        try
        {
            request.BeforeWrite();
            if (request.IsDirectory)
                FileSystem.DeleteDirectory(request.ItemPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
            else
                FileSystem.DeleteFile(request.ItemPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);

            return new ExplorerActionResult
            {
                State = ExplorerActionState.Success,
                EventType = "Delete",
                UserMessage = $"{itemName} を消しました。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Delete success: {itemName}({itemStatus}) in {itemParent}",
                HistoryMessage = $"{itemName} を消しました。",
                HistoryOutcome = HistoryOutcome.Success,
                Source = "ExplorerActionService.DeleteItem",
                TargetName = itemName,
                PathText = itemParent,
                Note = $"削除前: {request.ItemPath}",
                SourcePath = request.ItemPath,
                SourceParent = itemParent,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.Failure,
                EventType = "Delete",
                UserMessage = $"{itemName} を消せませんでした。",
                LogMessage = $"Explorer[{request.ModeLabel}]: Delete failed: {itemName}({itemStatus}) in {itemParent}: {ex.Message}",
                HistoryMessage = $"{itemName} を消せませんでした。",
                HistoryOutcome = HistoryOutcome.Failure,
                Source = "ExplorerActionService.DeleteItem",
                TargetName = itemName,
                PathText = itemParent,
                Note = $"削除失敗: {request.ItemPath}",
                SourcePath = request.ItemPath,
                SourceParent = itemParent,
            };
        }
    }

    public static ExplorerActionResult CreateFolder(CreateFolderRequest request)
    {
        SwkLogger.Info($"Explorer[{request.ModeLabel}]: CreateFolder attempt: {request.FolderName} in {request.ParentFolder}");
        if (string.IsNullOrWhiteSpace(request.FolderName))
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.NoChange,
                EventType = "CreateFolder",
                Source = "ExplorerActionService.CreateFolder",
            };
        }

        if (!Directory.Exists(request.ParentFolder))
        {
            return Blocked("CreateFolder", "その場所が見つかりません。",
                $"Explorer[{request.ModeLabel}]: CreateFolder blocked - parent not found: {request.ParentFolder}",
                targetName: request.FolderName,
                pathText: request.ParentFolder);
        }

        if (request.FolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Blocked("CreateFolder", "その名前では作れません。",
                $"Explorer[{request.ModeLabel}]: CreateFolder blocked - invalid name: {request.FolderName}",
                targetName: request.FolderName,
                pathText: request.ParentFolder);
        }

        string destinationPath = Path.Combine(request.ParentFolder, request.FolderName);
        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
        {
            return Blocked("CreateFolder", "同じ名前があるので作れません。",
                $"Explorer[{request.ModeLabel}]: CreateFolder blocked - same name: {destinationPath}",
                targetName: request.FolderName,
                pathText: request.ParentFolder);
        }

        string parentStatus = request.GetShareStatus(request.ParentFolder);
        try
        {
            request.BeforeWrite();
            Directory.CreateDirectory(destinationPath);

            return new ExplorerActionResult
            {
                State = ExplorerActionState.Success,
                EventType = "CreateFolder",
                UserMessage = $"{request.FolderName} を作りました。",
                LogMessage = $"Explorer[{request.ModeLabel}]: CreateFolder success: {request.FolderName} in {request.ParentFolder}(parent:{parentStatus})",
                HistoryMessage = $"{request.FolderName} を作りました。",
                HistoryOutcome = HistoryOutcome.Success,
                Source = "ExplorerActionService.CreateFolder",
                TargetName = request.FolderName,
                PathText = request.ParentFolder,
                Note = $"親フォルダー共有状況: {parentStatus}",
                SourceParent = request.ParentFolder,
                DestinationPath = destinationPath,
                DestinationFolder = request.ParentFolder,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExplorerActionResult
            {
                State = ExplorerActionState.Failure,
                EventType = "CreateFolder",
                UserMessage = "作れませんでした。",
                LogMessage = $"Explorer[{request.ModeLabel}]: CreateFolder failed: {request.FolderName} in {request.ParentFolder}: {ex.Message}",
                HistoryMessage = $"{request.FolderName} を作れませんでした。",
                HistoryOutcome = HistoryOutcome.Failure,
                Source = "ExplorerActionService.CreateFolder",
                TargetName = request.FolderName,
                PathText = request.ParentFolder,
                SourceParent = request.ParentFolder,
                DestinationPath = destinationPath,
                DestinationFolder = request.ParentFolder,
            };
        }
    }

    private static ExplorerActionResult Blocked(string eventType, string userMessage, string logMessage,
        string? targetName = null, string? pathText = null, string? note = null)
        => new()
        {
            State = ExplorerActionState.Blocked,
            EventType = eventType,
            UserMessage = userMessage,
            LogMessage = logMessage,
            HistoryMessage = userMessage,
            HistoryOutcome = HistoryOutcome.Warning,
            Source = "ExplorerActionService",
            TargetName = targetName,
            PathText = pathText,
            Note = note,
        };

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)));
        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory))
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
    }
}
