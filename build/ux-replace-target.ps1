# Minimal external text target for in-place replacement UI verification.
param(
    [string]$Text = 'Hello world'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = [Windows.Forms.Form]::new()
$form.Text = 'TranslationApp replacement target'
$form.Size = [Drawing.Size]::new(520, 260)
$form.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen

$form.Add_Shown({
    $form.Activate()
    $textBox.Focus()
})
$textBox = [Windows.Forms.TextBox]::new()
$textBox.Dock = [Windows.Forms.DockStyle]::Fill
$textBox.Multiline = $true
$textBox.AcceptsReturn = $true
$textBox.Text = $Text
$textBox.SelectAll()

$form.Controls.Add($textBox)
[Windows.Forms.Application]::Run($form)
