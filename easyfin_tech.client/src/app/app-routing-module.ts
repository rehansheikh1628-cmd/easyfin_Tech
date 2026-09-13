import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';

import { HomeComponent } from './pages/home/home.component';
import { HowItWorksComponent } from './pages/how-it-works/how-it-works.component';
import { FeaturesComponent } from './pages/features/features.component';
import { SupportedBanksComponent } from './pages/supported-banks/supported-banks.component';
import { LoginComponent } from './pages/login/login.component';
import { SignupComponent } from './pages/signup/signup.component';

import { AppLayoutComponent } from './layout/app-layout/app-layout.component';
import { DashboardComponent } from './pages/dashboard/dashboard.component';
import { ConverterComponent } from './pages/converter/converter.component';
import { FilesComponent } from './pages/files/files.component';
import { SettingsComponent } from './pages/settings/settings.component';
import { ExcelToTallyComponent } from './pages/excel-to-tally/excel-to-tally.component';
import { AuthGuard } from './guards/auth.guard';

const routes: Routes = [
  // Public Routes
  { path: '', component: HomeComponent, pathMatch: 'full' },
  { path: 'how-it-works', component: HowItWorksComponent },
  { path: 'features', component: FeaturesComponent },
  { path: 'supported-banks', component: SupportedBanksComponent },
  { path: 'login', component: LoginComponent },
  { path: 'signup', component: SignupComponent },

  // App Shell & Application Routes (Protected)
  {
    path: '',
    component: AppLayoutComponent,
    canActivate: [AuthGuard],
    children: [
      { path: 'dashboard', component: DashboardComponent },
      { path: 'converter', component: ConverterComponent },
      { path: 'excel-to-tally', component: ExcelToTallyComponent },
      { path: 'files', component: FilesComponent },
      { path: 'settings', component: SettingsComponent }
    ]
  },

  // Fallback
  { path: '**', redirectTo: '' }
];

@NgModule({
  imports: [RouterModule.forRoot(routes, { scrollPositionRestoration: 'enabled' })],
  exports: [RouterModule]
})
export class AppRoutingModule { }
