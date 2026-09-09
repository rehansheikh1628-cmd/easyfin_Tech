import { HttpClientModule, HTTP_INTERCEPTORS } from '@angular/common/http';
import { NgModule, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { AppRoutingModule } from './app-routing-module';
import { App } from './app';
import { AuthInterceptor } from './services/auth.interceptor';

// Layout Components
import { PublicNavComponent } from './layout/public-nav/public-nav.component';
import { PublicFooterComponent } from './layout/public-footer/public-footer.component';
import { AppLayoutComponent } from './layout/app-layout/app-layout.component';

// Public Pages
import { HomeComponent } from './pages/home/home.component';
import { HowItWorksComponent } from './pages/how-it-works/how-it-works.component';
import { FeaturesComponent } from './pages/features/features.component';
import { SupportedBanksComponent } from './pages/supported-banks/supported-banks.component';
import { PricingComponent } from './pages/pricing/pricing.component';
import { LoginComponent } from './pages/login/login.component';
import { SignupComponent } from './pages/signup/signup.component';

// Application Pages
import { DashboardComponent } from './pages/dashboard/dashboard.component';
import { ConverterComponent } from './pages/converter/converter.component';
import { FilesComponent } from './pages/files/files.component';
import { SettingsComponent } from './pages/settings/settings.component';
import { ExcelToTallyComponent } from './pages/excel-to-tally/excel-to-tally.component';

@NgModule({
  declarations: [
    App,
    PublicNavComponent,
    PublicFooterComponent,
    AppLayoutComponent,
    HomeComponent,
    HowItWorksComponent,
    FeaturesComponent,
    SupportedBanksComponent,
    PricingComponent,
    LoginComponent,
    SignupComponent,
    DashboardComponent,
    ConverterComponent,
    ExcelToTallyComponent,
    FilesComponent,
    SettingsComponent
  ],
  imports: [
    BrowserModule,
    CommonModule,
    FormsModule,
    HttpClientModule,
    AppRoutingModule
  ],
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    {
      provide: HTTP_INTERCEPTORS,
      useClass: AuthInterceptor,
      multi: true
    }
  ],
  bootstrap: [App]
})
export class AppModule { }
