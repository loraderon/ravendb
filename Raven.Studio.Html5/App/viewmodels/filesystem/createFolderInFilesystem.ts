﻿import app = require("durandal/app");
import collection = require("models/collection");
import dialogViewModelBase = require("viewmodels/dialogViewModelBase");
import dialog = require("plugins/dialog");
import commandBase = require("commands/commandBase");

import filesystem = require("models/filesystem/filesystem");
import createFilesystemCommand = require("commands/filesystem/createFilesystemCommand");

class createFolderInFilesystem extends dialogViewModelBase {

    public creationTask = $.Deferred();
    creationTaskStarted = false;

    public folderName = ko.observable('');

    private folders : string[];
    private newCommandBase = new commandBase();

    constructor(folders) {
        super();
        this.folders = folders;
    }

    cancel() {
        dialog.close(this);
    }

    attached() {
        super.attached();
        //this.folderName(true);
    }

    deactivate() {
        // If we were closed via X button or other dialog dismissal, reject the deletion task since
        // we never started it.
        if (!this.creationTaskStarted) {
            this.creationTask.reject();
        }
    }

    create() {
        var folderName = this.folderName();

        if (this.isClientSideInputOK(folderName)) {
            this.creationTaskStarted = true;
            this.creationTask.resolve(folderName);
            dialog.close(this);
        }
    }

    private isClientSideInputOK(folderName): boolean {
        var errorMessage;

        if (folderName == null) {
            errorMessage = "Please fill in the folder Name";
        }
        else if (this.folderExists(folderName, this.folders) === true) {
            errorMessage = "Folder already exists!";
        }

        if (errorMessage != null) {
            this.newCommandBase.reportError(errorMessage);
            return false;
        }
        return true;
    }

    private folderExists(folderName: string, folders: string[]): boolean {
        for (var i = 0; i < folders.length; i++) {
            if (folderName == folders[i]) {
                return true;
            }
        }
        return false;
    }
}

export = createFolderInFilesystem;